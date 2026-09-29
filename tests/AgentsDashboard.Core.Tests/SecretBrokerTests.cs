using System.Net;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Secrets;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public sealed class SecretBrokerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly MemoryVault _vault = new();
    private readonly FakeClock _clock = new();
    private readonly RecordingHandler _http = new();
    private readonly SecretBroker _broker;

    private static SecretNeed Read(string name) => new() { Name = name, Read = true };

    private static SecretNeed Send(string name, params string[] hosts) => new() { Name = name, Hosts = hosts };

    private static readonly SecretCaller Ext = new("tracker", "Tracker", "hash-1",
    [
        Send("github", "api.github.com", "uploads.github.com"),
        Send("build", "intranet.corp:8443"),
        Send("jira", "example.atlassian.net"),
        Read("linear"),
    ]);

    private static readonly SecretCaller ExtRebuilt = Ext with { Hash = "hash-2" };
    private static readonly SecretCaller Other = new("other", "Other", "hash-9", [Read("linear")]);
    private static readonly SecretPlacement Bearer = new("Authorization", "Bearer", false);

    public SecretBrokerTests() => _broker = New();

    private SecretBroker New() => new(new SecretCatalog(new AppPaths(_temp.Path)), _vault, _clock, _http);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _broker.Dispose();
        _temp.Dispose();
    }

    /// <summary>Waits for the broker to put a request up, as the prompt would see it.</summary>
    private async Task<SecretRequest> Prompted(int count = 1)
    {
        for (var i = 0; i < 200 && _broker.Pending.Count < count; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(count, _broker.Pending.Count);
        return _broker.Pending[^1];
    }

    private Task<HttpResponseMessage?> Get(SecretCaller caller, string name, string url) =>
        _broker.SendAsync(caller, name, new HttpRequestMessage(HttpMethod.Get, url), Bearer, Ct);

    /// <summary>Makes the first call and binds it from the prompt.</summary>
    private async Task<string?> ReadBound(SecretCaller caller, string need, string secret)
    {
        var read = _broker.ReadAsync(caller, need, Ct);
        Assert.Null(_broker.Allow(await Prompted(), secret));
        return await read;
    }

    [Fact]
    public async Task A_read_waits_for_a_binding_and_then_gets_the_value()
    {
        Assert.Null(_broker.Store("work-linear", "lin_key"));

        var read = _broker.ReadAsync(Ext, "linear", Ct);
        var request = await Prompted();

        Assert.False(read.IsCompleted);
        Assert.Equal("linear", request.Name);
        Assert.True(request.Need.Read);

        // Your name and the extension's need not match; the binding maps them.
        Assert.Null(_broker.Allow(request, "work-linear"));
        Assert.Equal("lin_key", await read);
        Assert.Empty(_broker.Pending);
        Assert.Equal("work-linear", _broker.Binding("tracker", "linear")!.Secret);
    }

    [Fact]
    public async Task Once_bound_the_same_build_is_answered_without_asking()
    {
        _broker.Store("linear", "k");
        await ReadBound(Ext, "linear", "linear");

        Assert.Equal("k", await _broker.ReadAsync(Ext, "linear", Ct));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task A_rebuilt_extension_is_asked_again()
    {
        _broker.Store("linear", "k");
        await ReadBound(Ext, "linear", "linear");

        var again = _broker.ReadAsync(ExtRebuilt, "linear", Ct);
        var request = await Prompted();

        Assert.Equal("hash-2", request.Caller.Hash);
        Assert.False(again.IsCompleted);
    }

    [Fact]
    public async Task A_binding_is_for_one_extension_only()
    {
        _broker.Store("linear", "k");
        await ReadBound(Ext, "linear", "linear");

        var other = _broker.ReadAsync(Other, "linear", Ct);
        Assert.Equal("other", (await Prompted()).Caller.ExtensionId);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task A_name_the_manifest_does_not_declare_throws_without_a_prompt()
    {
        _broker.Store("aws", "secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ReadAsync(Ext, "aws", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Get(Ext, "aws", "https://api.github.com/user"));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task A_host_the_need_does_not_declare_throws_without_a_prompt()
    {
        _broker.Store("github", "ghp_x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Get(Ext, "github", "https://collector.example/steal"));
        Assert.Empty(_broker.Pending);
        Assert.Empty(_http.Sent);
    }

    [Fact]
    public async Task A_port_is_part_of_a_declared_host()
    {
        _broker.Store("build", "tok");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Get(Ext, "build", "https://intranet.corp/api"));

        var send = Get(Ext, "build", "https://intranet.corp:8443/api");
        _broker.Allow(await Prompted(), "build");
        (await send)!.Dispose();
        Assert.Equal(["Bearer tok"], _http.Authorizations);
    }

    [Fact]
    public async Task A_need_without_read_cannot_be_read()
    {
        _broker.Store("github", "ghp_x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ReadAsync(Ext, "github", Ct));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Binding_in_settings_before_the_first_call_means_no_prompt()
    {
        _broker.Store("gh-work", "ghp_x");
        Assert.Null(_broker.Bind("tracker", "hash-1", Ext.Need("github")!, "gh-work"));

        using var response = await Get(Ext, "github", "https://api.github.com/user");

        Assert.NotNull(response);
        Assert.Empty(_broker.Pending);
        Assert.Equal(["Bearer ghp_x"], _http.Authorizations);
    }

    [Fact]
    public async Task Binding_in_settings_answers_a_call_that_is_waiting()
    {
        _broker.Store("gh-work", "ghp_x");
        var send = Get(Ext, "github", "https://api.github.com/user");
        await Prompted();

        _broker.Bind("tracker", "hash-1", Ext.Need("github")!, "gh-work");

        (await send)!.Dispose();
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task A_binding_covers_every_host_the_need_declares()
    {
        _broker.Store("github", "ghp_x");
        var first = Get(Ext, "github", "https://api.github.com/a");
        _broker.Allow(await Prompted(), "github");
        (await first)!.Dispose();

        (await Get(Ext, "github", "https://uploads.github.com/b"))!.Dispose();

        Assert.Empty(_broker.Pending);
        Assert.Equal(["api.github.com", "uploads.github.com"], _broker.Binding("tracker", "github")!.Hosts);
    }

    [Fact]
    public async Task A_host_added_to_the_manifest_after_binding_is_asked_about()
    {
        // The manifest is not in the code hash, so the same build can come back declaring more.
        _broker.Store("github", "ghp_x");
        _broker.Bind("tracker", "hash-1", Ext.Need("github")!, "github");
        var widened = Ext with { Needs = [Send("github", "api.github.com", "uploads.github.com", "gist.github.com")] };

        var send = Get(widened, "github", "https://gist.github.com/x");
        await Prompted();

        Assert.False(send.IsCompleted);
        Assert.Empty(_http.Sent);
    }

    [Fact]
    public async Task A_refusal_is_kept_so_a_timer_does_not_ask_again()
    {
        _broker.Store("linear", "k");
        var first = _broker.ReadAsync(Ext, "linear", Ct);
        _broker.Refuse(await Prompted());

        Assert.Null(await first);
        Assert.Null(await _broker.ReadAsync(Ext, "linear", Ct));
        Assert.Empty(_broker.Pending);
        Assert.True(_broker.Binding("tracker", "linear")!.Refused);
    }

    [Fact]
    public async Task A_refusal_needs_no_stored_secret()
    {
        var first = _broker.ReadAsync(Ext, "linear", Ct);
        _broker.Refuse(await Prompted());

        Assert.Null(await first);
        Assert.Empty(_broker.Secrets);
        Assert.Null(await _broker.ReadAsync(Ext, "linear", Ct));
    }

    [Fact]
    public async Task Unbinding_a_refusal_lets_it_ask_again()
    {
        var first = _broker.ReadAsync(Ext, "linear", Ct);
        _broker.Refuse(await Prompted());
        await first;

        _broker.Unbind("tracker", "linear");
        _ = _broker.ReadAsync(Ext, "linear", Ct);

        await Prompted();
    }

    [Fact]
    public async Task Not_now_answers_null_and_keeps_nothing()
    {
        _broker.Store("linear", "k");
        var first = _broker.ReadAsync(Ext, "linear", Ct);
        _broker.Dismiss(await Prompted());

        Assert.Null(await first);
        Assert.Empty(_broker.Bindings);

        _ = _broker.ReadAsync(Ext, "linear", Ct);
        await Prompted();
    }

    [Fact]
    public async Task Calls_for_the_same_need_share_one_prompt()
    {
        _broker.Store("github", "ghp_x");
        var sends = new[] { "https://api.github.com/a", "https://uploads.github.com/b", "https://api.github.com/c" }
            .Select(url => Get(Ext, "github", url))
            .ToList();
        var request = await Prompted();

        _broker.Allow(request, "github");

        foreach (var response in await Task.WhenAll(sends))
        {
            response!.Dispose();
        }

        Assert.Equal(3, _http.Authorizations.Count);
    }

    [Fact]
    public async Task A_missing_secret_is_added_from_the_prompt()
    {
        var read = _broker.ReadAsync(Ext, "linear", Ct);
        var request = await Prompted();

        Assert.NotNull(_broker.Allow(request, "linear"));
        Assert.Null(_broker.Allow(request, "linear", "lin_key"));

        Assert.Equal("lin_key", await read);
        Assert.Equal("lin_key", _vault.Read("linear"));
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_and_the_prompt_stays()
    {
        using var cts = new CancellationTokenSource();
        var read = _broker.ReadAsync(Ext, "linear", cts.Token);
        var request = await Prompted();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Same(request, Assert.Single(_broker.Pending));
    }

    [Fact]
    public async Task Bindings_and_names_survive_a_restart_and_values_are_not_in_the_file()
    {
        _broker.Store("linear", "token-value");
        await ReadBound(Ext, "linear", "linear");

        using var again = New();
        Assert.Equal("token-value", await again.ReadAsync(Ext, "linear", Ct));

        var file = await File.ReadAllTextAsync(new AppPaths(_temp.Path).SecretsFile, Ct);
        Assert.Contains("\"linear\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_from_before_bindings_keeps_its_names_and_drops_its_grants()
    {
        var paths = new AppPaths(_temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SecretsFile)!);
        await File.WriteAllTextAsync(paths.SecretsFile, """
            [ { "Name": "jira", "Added": "2026-09-01T00:00:00+00:00",
                "Grants": [ { "ExtensionId": "tracker", "Hash": "hash-1", "Access": "Read" } ] } ]
            """, Ct);

        using var broker = New();

        Assert.Equal("jira", Assert.Single(broker.Secrets).Name);
        Assert.Empty(broker.Bindings);
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        _broker.Store("linear", "k");
        await ReadBound(Ext, "linear", "linear");
        var at = _clock.Now;

        _clock.Advance(TimeSpan.FromSeconds(30));
        await _broker.ReadAsync(Ext, "linear", Ct);
        Assert.Equal(at, _broker.Binding("tracker", "linear")!.LastUsed);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await _broker.ReadAsync(Ext, "linear", Ct);
        Assert.Equal(_clock.Now, _broker.Binding("tracker", "linear")!.LastUsed);
    }

    [Fact]
    public async Task Deleting_a_secret_removes_the_value_and_its_bindings()
    {
        _broker.Store("linear", "k");
        await ReadBound(Ext, "linear", "linear");

        Assert.Null(_broker.Delete("linear"));

        Assert.Null(_vault.Read("linear"));
        Assert.Empty(_broker.Secrets);
        Assert.Empty(_broker.Bindings);
        _ = _broker.ReadAsync(Ext, "linear", Ct);
        await Prompted();
    }

    [Fact]
    public async Task A_brokered_request_carries_the_secret_and_the_extension_never_gets_it()
    {
        _broker.Store("github", "ghp_x");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer something-the-extension-put");

        var send = _broker.SendAsync(Ext, "github", request, Bearer, Ct);
        _broker.Allow(await Prompted(), "github");

        using var response = await send;
        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.Equal(["Bearer ghp_x"], _http.Authorizations);
        Assert.Equal(SecretAccess.Brokered, _broker.Binding("tracker", "github")!.Access);
    }

    [Fact]
    public async Task The_extension_cannot_read_the_secret_back_off_its_request()
    {
        _broker.Store("github", "ghp_x");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.TryAddWithoutValidation("X-Own", "kept");
        var send = _broker.SendAsync(Ext, "github", request, Bearer, Ct);
        _broker.Allow(await Prompted(), "github");
        using var response = await send;

        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(response!.RequestMessage?.Headers.Contains("Authorization") ?? false);
        Assert.Equal(["Bearer ghp_x"], _http.Authorizations);
        Assert.Equal(["kept"], _http.Sent.Single().Headers.GetValues("X-Own"));
    }

    [Fact]
    public async Task Changing_the_request_while_the_prompt_is_up_does_not_move_the_secret()
    {
        _broker.Store("github", "ghp_x");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        var send = _broker.SendAsync(Ext, "github", request, Bearer, Ct);
        var prompt = await Prompted();

        request.RequestUri = new Uri("https://collector.example/steal");
        request.Headers.Host = "collector.example";
        _broker.Allow(prompt, "github");
        using var response = await send;

        var sent = _http.Sent.Single();
        Assert.Equal("api.github.com", sent.RequestUri!.Host);
        Assert.Null(sent.Headers.Host);
    }

    [Fact]
    public async Task A_read_binding_covers_brokered_requests_anywhere()
    {
        _broker.Store("linear", "lin_key");
        await ReadBound(Ext, "linear", "linear");

        using var response = await Get(Ext, "linear", "https://api.linear.app/graphql");

        Assert.NotNull(response);
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Basic_placement_encodes_the_value()
    {
        _broker.Store("jira", "me@example.com:tok");
        _broker.Bind("tracker", "hash-1", Ext.Need("jira")!, "jira");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.atlassian.net/rest/api/3/myself");
        (await _broker.SendAsync(Ext, "jira", request, new SecretPlacement("Authorization", "Basic", true), Ct))!.Dispose();

        Assert.Equal(["Basic bWVAZXhhbXBsZS5jb206dG9r"], _http.Authorizations);
    }

    [Theory]
    [InlineData("http://api.github.com/user")]
    [InlineData("/user")]
    public async Task A_brokered_request_must_be_https(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.RelativeOrAbsolute));

        await Assert.ThrowsAsync<ArgumentException>(() => _broker.SendAsync(Ext, "github", request, Bearer, Ct));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Nothing_an_agent_tool_starts_can_ask_for_a_secret()
    {
        _broker.Store("linear", "lin_key");
        await ReadBound(Ext, "linear", "linear");

        Task<string?> inside, started;
        using (SecretBroker.ForAgent())
        {
            inside = _broker.ReadAsync(Ext, "linear", Ct);
            started = Task.Run(() => _broker.ReadAsync(Ext, "linear", Ct), Ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => inside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => started);
        Assert.Equal("lin_key", await _broker.ReadAsync(Ext, "linear", Ct));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public void The_store_itself_refuses_an_agent_reading_or_writing()
    {
        var vault = SecretVaults.Guarded(_vault);
        vault.Write("github", "ghp_x");

        using (SecretBroker.ForAgent())
        {
            Assert.Throws<InvalidOperationException>(() => vault.Read("github"));
            Assert.Throws<InvalidOperationException>(() => vault.Write("github", "replaced"));
            Assert.Throws<InvalidOperationException>(() => vault.Delete("github"));
        }

        Assert.Equal("ghp_x", vault.Read("github"));
    }

    [Theory]
    [InlineData("github", true)]
    [InlineData("jira-cloud.work_2", true)]
    [InlineData("GitHub", false)]
    [InlineData("-lead", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void Names_are_lower_case_and_plain(string name, bool ok) =>
        Assert.Equal(ok, SecretNames.Problem(name) is null);

    [Theory]
    [InlineData("api.github.com", "api.github.com")]
    [InlineData("API.GitHub.com", "api.github.com")]
    [InlineData("api.github.com:443", "api.github.com")]
    [InlineData("intranet.corp:8443", "intranet.corp:8443")]
    [InlineData("https://api.github.com", null)]
    [InlineData("api.github.com/user", null)]
    [InlineData("*.github.com", null)]
    [InlineData("me@api.github.com", null)]
    [InlineData("", null)]
    public void Hosts_are_written_one_way(string text, string? host) =>
        Assert.Equal(host, SecretNames.Host(text));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Authorizations { get; } = [];

        public List<HttpRequestMessage> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Authorizations)
            {
                Authorizations.AddRange(request.Headers.TryGetValues("Authorization", out var values) ? values : ["(none)"]);
                Sent.Add(request);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

/// <summary>
/// The real OS store. Off unless AGENTS_DASHBOARD_VAULT_TESTS=1: it writes to
/// the login keychain, which in CI may be locked or absent, and on macOS the
/// first run may ask.
/// </summary>
public class SecretVaultTests
{
    [Fact]
    public void Round_trips_a_value_through_this_machines_store()
    {
        if (Environment.GetEnvironmentVariable("AGENTS_DASHBOARD_VAULT_TESTS") != "1")
        {
            Assert.Skip("Set AGENTS_DASHBOARD_VAULT_TESTS=1 to use the OS store.");
        }

        var vault = SecretVaults.ForThisMachine();
        var name = "test-" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            Assert.Null(vault.Read(name));
            vault.Write(name, "first é");
            Assert.Equal("first é", vault.Read(name));
            vault.Write(name, "second");
            Assert.Equal("second", vault.Read(name));
        }
        finally
        {
            vault.Delete(name);
        }

        Assert.Null(vault.Read(name));
        vault.Delete(name);
    }
}
