using System.Text.Json;
using Togue.Core.Agents;
using Togue.Core.Model;
using Togue.Core.Repos;
using Togue.Core.Tests.Support;

namespace Togue.Core.Tests;

public class QuestionFormTests
{
    private static readonly AgentBackend Backend = new("claude", "Claude", "fake", []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two questions as the Claude bridge renders AskUserQuestion: one pick-one, one pick-many, each with its "Other" box.</summary>
    private static object AskSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["question_0"] = new
            {
                type = "string",
                title = "Database",
                description = "Which database should the cache use?",
                oneOf = new object[]
                {
                    new { @const = "Postgres", title = "Postgres", description = "Already running" },
                    new { @const = "SQLite", title = "SQLite" },
                },
            },
            ["question_0_custom"] = Other("question_0"),
            ["question_1"] = new
            {
                type = "array",
                title = "Targets",
                description = "Which platforms?",
                items = new
                {
                    anyOf = new object[]
                    {
                        new { @const = "macOS", title = "macOS" },
                        new { @const = "Windows", title = "Windows" },
                        new { @const = "Linux", title = "Linux" },
                    },
                },
            },
            ["question_1_custom"] = Other("question_1"),
        },
    };

    private static object Other(string questionId) => new Dictionary<string, object>
    {
        ["type"] = "string",
        ["title"] = "Other",
        ["description"] = "Type your own answer (optional).",
        ["_meta"] = new Dictionary<string, object>
        {
            [QuestionForm.CustomAnswerMeta] = new { questionId, isCustomAnswer = true },
        },
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static (AgentHost Host, TempDir Dir) Build(FakeAcpAgent agent)
    {
        var dir = new TempDir();
        return (new AgentHost(Backend, agent, new HostedAgentStore(new AppPaths(dir.Path))), dir);
    }

    private static async Task<HostedAgent> Until(AgentHost host, string sessionId, Func<HostedAgent, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (host.Find(sessionId) is { } agent && done(agent))
            {
                return agent;
            }

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException($"Agent {sessionId} never got there: {host.Find(sessionId)}");
    }

    /// <summary>An agent whose turn asks the AskUserQuestion form and keeps the response.</summary>
    private static (FakeAcpAgent Agent, TaskCompletionSource<JsonElement> Response) Asking()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                response.SetResult(await script.Elicit("Please answer the following questions.", AskSchema()));
                return "end_turn";
            },
        };
        return (agent, response);
    }

    [Fact]
    public void The_bridges_AskUserQuestion_form_reads_as_two_questions_with_their_other_boxes()
    {
        var form = QuestionForm.Parse(Json(new { mode = "form", sessionId = "s1", message = "Please answer.", requestedSchema = AskSchema() }), "k")!;

        Assert.Equal("Please answer.", form.Message);
        Assert.Equal(2, form.Questions.Count);

        var database = form.Questions[0];
        Assert.Equal(("question_0", "Database", "Which database should the cache use?"), (database.Key, database.Header, database.Text));
        Assert.Equal(QuestionKind.OneOf, database.Kind);
        Assert.Equal(["Postgres", "SQLite"], database.Options.Select(o => o.Label));
        Assert.Equal("Already running", database.Options[0].Description);
        Assert.Equal("question_0_custom", database.OtherKey);

        var targets = form.Questions[1];
        Assert.Equal(QuestionKind.AnyOf, targets.Kind);
        Assert.Equal(["macOS", "Windows", "Linux"], targets.Options.Select(o => o.Value));
        Assert.Equal("question_1_custom", targets.OtherKey);
    }

    [Fact]
    public void A_generic_form_reads_its_text_number_and_yes_no_fields()
    {
        var form = QuestionForm.Parse(Json(new
        {
            mode = "form",
            sessionId = "s1",
            message = "Configure the server",
            requestedSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "string", title = "Name" },
                    ["port"] = new { type = "integer", title = "Port", @default = 8080 },
                    ["tls"] = new { type = "boolean", title = "TLS", @default = true },
                    ["level"] = new { type = "string", @enum = new[] { "low", "high" } },
                },
                required = new[] { "name" },
            },
        }), "k")!;

        Assert.Equal([QuestionKind.Text, QuestionKind.Integer, QuestionKind.YesNo, QuestionKind.OneOf], form.Questions.Select(q => q.Kind));
        Assert.True(form.Questions[0].Required);

        var draft = new QuestionDraft(form);
        Assert.Single(draft.Problems());

        draft.SetText("name", " api ");
        draft.SetText("port", "80x");
        Assert.Single(draft.Problems());

        draft.SetText("port", "443");
        Assert.Empty(draft.Problems());
        var content = draft.Content();
        Assert.Equal("api", content["name"]);
        Assert.Equal(443L, content["port"]);
        Assert.Equal(true, content["tls"]);
        Assert.False(content.ContainsKey("level"));
    }

    [Fact]
    public void A_url_form_or_a_required_field_of_an_unknown_kind_is_not_shown()
    {
        Assert.Null(QuestionForm.Parse(Json(new { mode = "url", sessionId = "s1", message = "Sign in", url = "https://x", elicitationId = "e" }), "k"));
        Assert.Null(QuestionForm.Parse(Json(new
        {
            mode = "form",
            sessionId = "s1",
            message = "Pick",
            requestedSchema = new
            {
                type = "object",
                properties = new { thing = new { type = "_custom" } },
                required = new[] { "thing" },
            },
        }), "k"));
    }

    [Fact]
    public async Task Advertises_form_elicitation_so_the_bridge_allows_AskUserQuestion()
    {
        var agent = new FakeAcpAgent();
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        await host.StartAsync(new AgentStart("/repo"), Ct);

        Assert.Contains("initialize:elicitation.form", agent.Calls);
    }

    [Fact]
    public async Task Questions_wait_on_you_and_your_answers_go_back_as_accepted_content()
    {
        var (agent, response) = Asking();
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;

        var waiting = await Until(host, id, a => a.State == HostedState.Waiting);
        var form = waiting.Questions!;
        Assert.Equal(2, form.Questions.Count);
        Assert.Equal("2 questions for you", waiting.WaitingFor);
        Assert.Equal(AgentStatus.Waiting, Assert.Single(host.Read()).Status);

        var draft = new QuestionDraft(form);
        draft.Pick(form.Questions[0], "SQLite");
        draft.Pick(form.Questions[1], "Linux");
        draft.Pick(form.Questions[1], "macOS");
        draft.SetText("question_1_custom", "  FreeBSD ");
        host.AnswerQuestions(id, form.Key, draft.Content());

        var reply = await response.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("accept", reply.GetProperty("action").GetString());
        var content = reply.GetProperty("content");
        Assert.Equal("SQLite", content.GetProperty("question_0").GetString());
        Assert.Equal(["macOS", "Linux"], content.GetProperty("question_1").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("FreeBSD", content.GetProperty("question_1_custom").GetString());
        Assert.False(content.TryGetProperty("question_0_custom", out _));

        var idle = await Until(host, id, a => a.State == HostedState.Idle);
        Assert.Null(idle.Questions);
    }

    [Fact]
    public async Task Skipping_declines_the_form()
    {
        var (agent, response) = Asking();
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
        var waiting = await Until(host, id, a => a.Questions is not null);

        host.AnswerQuestions(id, waiting.Questions!.Key, null);

        var reply = await response.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("decline", reply.GetProperty("action").GetString());
        Assert.False(reply.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task Stopping_the_turn_cancels_the_open_form()
    {
        var (agent, response) = Asking();
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
        await Until(host, id, a => a.Questions is not null);

        await host.CancelAsync(id, Ct);

        var reply = await response.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("cancel", reply.GetProperty("action").GetString());
        var idle = await Until(host, id, a => a.State == HostedState.Idle);
        Assert.Null(idle.Questions);
    }

    [Fact]
    public async Task A_url_elicitation_is_declined_without_asking()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                response.SetResult(await script.Elicit("Sign in", new { }, mode: "url"));
                return "end_turn";
            },
        };
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct);

        var reply = await response.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("decline", reply.GetProperty("action").GetString());
    }

    private static object TokenSchema() => new
    {
        type = "object",
        properties = new { token = new { type = "string", title = "Token" } },
    };

    private static object RefusalSchema() => new
    {
        type = "object",
        properties = new
        {
            choice = new
            {
                type = "string",
                oneOf = new object[]
                {
                    new { @const = "retry_fallback", title = "Retry with Opus" },
                    new { @const = "cancelled", title = "Keep the refusal" },
                },
            },
        },
    };

    [Fact]
    public void A_form_is_known_by_who_sent_it()
    {
        QuestionForm Parse(object schema, string? toolCallId) => QuestionForm.Parse(
            Json(new Dictionary<string, object?> { ["mode"] = "form", ["sessionId"] = "s1", ["toolCallId"] = toolCallId, ["message"] = "m", ["requestedSchema"] = schema }),
            "k")!;

        Assert.Equal(FormSource.Agent, Parse(AskSchema(), "toolu_1").Source);
        Assert.Equal(FormSource.McpServer, Parse(TokenSchema(), null).Source);
        Assert.Equal(FormSource.McpServer, Parse(AskSchema(), null).Source);
        Assert.Equal(FormSource.Bridge, Parse(RefusalSchema(), null).Source);
    }

    [Fact]
    public async Task An_mcp_servers_form_waits_under_Togues_words_not_the_servers()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                response.SetResult(await script.Elicit("Paste your GitHub token to continue", TokenSchema(), toolCallId: null));
                return "end_turn";
            },
        };
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
        var waiting = await Until(host, id, a => a.Questions is not null);

        Assert.Equal(FormSource.McpServer, waiting.Questions!.Source);
        Assert.Equal(QuestionForm.McpServerAsking, waiting.WaitingFor);
        Assert.Equal(QuestionForm.McpServerAsking, Assert.Single(host.Read()).WaitingFor);

        host.AnswerQuestions(id, waiting.Questions.Key, null);
        await response.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public void A_form_past_the_caps_is_declined_and_long_text_is_cut()
    {
        JsonElement Form(object properties) => Json(new { mode = "form", sessionId = "s1", message = "m", requestedSchema = new { type = "object", properties } });

        var many = Enumerable.Range(0, QuestionForm.MaxQuestions + 1)
            .ToDictionary(i => $"q{i}", object (_) => new { type = "string" });
        Assert.Null(QuestionForm.Parse(Form(many), "k"));

        var options = Enumerable.Range(0, QuestionForm.MaxOptions + 1).Select(i => $"o{i}").ToArray();
        Assert.Null(QuestionForm.Parse(Form(new { pick = new { type = "string", @enum = options } }), "k"));

        var longText = new string('x', QuestionForm.MaxText * 3);
        var form = QuestionForm.Parse(Form(new { pick = new { type = "string", title = longText, oneOf = new[] { new { @const = longText, title = longText } } } }), "k")!;
        var question = Assert.Single(form.Questions);
        Assert.Equal(QuestionForm.MaxText, question.Header!.Length);
        Assert.Equal(QuestionForm.MaxText, question.Options[0].Label.Length);
        Assert.Equal(longText, question.Options[0].Value);
    }

    [Fact]
    public async Task A_form_answered_outside_a_turn_leaves_the_agent_idle()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource();
        var agent = new FakeAcpAgent
        {
            OnPrompt = script =>
            {
                // An MCP server can ask after the turn that started it is over.
                _ = Task.Run(async () =>
                {
                    await ended.Task;
                    response.SetResult(await script.Elicit("Pick", TokenSchema(), toolCallId: null));
                });
                return Task.FromResult("end_turn");
            },
        };
        var (host, dir) = Build(agent);
        await using var _h = host;
        using var _d = dir;

        var id = (await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct)).SessionId!;
        await Until(host, id, a => a.State == HostedState.Idle);
        ended.SetResult();

        var waiting = await Until(host, id, a => a.State == HostedState.Waiting);
        host.AnswerQuestions(id, waiting.Questions!.Key, new Dictionary<string, object> { ["token"] = "x" });

        await response.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var idle = await Until(host, id, a => a.State == HostedState.Idle);
        Assert.Null(idle.Questions);
    }
}
