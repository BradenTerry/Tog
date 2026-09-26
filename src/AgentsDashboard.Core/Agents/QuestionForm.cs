using System.Globalization;
using System.Text.Json;

namespace AgentsDashboard.Core.Agents;

/// <summary>How a question in a form is answered.</summary>
public enum QuestionKind
{
    /// <summary>Pick one of the options.</summary>
    OneOf,

    /// <summary>Pick any number of the options.</summary>
    AnyOf,

    /// <summary>Type some text.</summary>
    Text,

    /// <summary>Type a number, which may have a fraction.</summary>
    Number,

    /// <summary>Type a whole number.</summary>
    Integer,

    /// <summary>Tick or leave unticked.</summary>
    YesNo,
}

/// <summary>Who is asking: it decides how much the form's own words are trusted on screen.</summary>
public enum FormSource
{
    /// <summary>Claude, through its AskUserQuestion tool. The bridge ties these to the tool call.</summary>
    Agent,

    /// <summary>
    /// A third-party MCP server the agent uses. Its wording is the server's, so
    /// it is never shown as if the agent or the dashboard said it.
    /// </summary>
    McpServer,

    /// <summary>The bridge itself: asking whether to retry a refused request on another model.</summary>
    Bridge,
}

/// <summary>One answer a question offers.</summary>
/// <param name="Value">What goes back to the agent when it is picked.</param>
/// <param name="Label">What it is called on screen.</param>
public sealed record QuestionOption(string Value, string Label, string? Description);

/// <summary>One question of a form the agent is asking you.</summary>
/// <param name="Key">The form field it answers.</param>
/// <param name="Header">A short label for it, when the agent gave one.</param>
/// <param name="Text">The question itself, when the form's message does not already say it.</param>
/// <param name="OtherKey">The field for a free-text answer of your own beside the options, if it has one.</param>
/// <param name="OtherHint">What the agent says the free-text box is for.</param>
/// <param name="Default">The value the agent suggested, as text: an option value, a number, "true" or "false".</param>
/// <param name="Defaults">For <see cref="QuestionKind.AnyOf"/>, the options the agent suggested.</param>
public sealed record Question(
    string Key,
    string? Header,
    string? Text,
    QuestionKind Kind,
    IReadOnlyList<QuestionOption> Options,
    bool Required,
    string? OtherKey = null,
    string? OtherHint = null,
    string? Format = null,
    string? Default = null,
    IReadOnlyList<string>? Defaults = null);

/// <summary>
/// A form the agent wants filled in, from ACP's <c>elicitation/create</c>: the
/// questions of Claude's AskUserQuestion tool, or whatever an MCP server or the
/// bridge itself asks.
/// </summary>
/// <remarks>
/// <para>
/// ACP forms are a flat JSON Schema object of primitive fields: strings (free,
/// or a pick from <c>oneOf</c> or <c>enum</c>), numbers, integers, booleans, and
/// arrays of picks. The bridge renders AskUserQuestion as one pick field per
/// question (<c>question_0</c>, ...), each followed by an optional free-text
/// field marked with <c>_meta._askUserQuestionCustomAnswer</c>. That field is
/// folded into its question as its "Other" box rather than shown as a question of
/// its own, so a form of three questions reads as three questions.
/// </para>
/// <para>
/// A field of a kind the dashboard does not know is left out when it is
/// optional. When it is required the form cannot be answered honestly, so
/// <see cref="Parse"/> gives up and the host declines it.
/// </para>
/// </remarks>
public sealed record QuestionForm(string Key, string Message, IReadOnlyList<Question> Questions, FormSource Source = FormSource.Agent)
{
    /// <summary>
    /// The most questions a form may ask. The chat redraws every second, and a
    /// form of thousands of fields from a careless or hostile MCP server would
    /// stall the page, so a larger form is declined rather than drawn.
    /// </summary>
    public const int MaxQuestions = 20;

    /// <summary>The most options one question may offer. Past it the form is declined.</summary>
    public const int MaxOptions = 50;

    /// <summary>Longer titles, descriptions, labels and messages are cut to this.</summary>
    public const int MaxText = 2000;

    /// <summary>What the dashboard says of a form from an MCP server, in place of the server's own words.</summary>
    public const string McpServerAsking = "An MCP server the agent uses is asking";

    /// <summary>
    /// Who sent a form. The bridge sets <c>toolCallId</c> only on AskUserQuestion;
    /// the refusal fallback has a fixed shape, one <c>choice</c> field between
    /// <c>retry_fallback</c> and <c>cancelled</c>; anything else is an MCP server's,
    /// passed through as the server wrote it. An MCP server can copy the fallback's
    /// shape, but then all it gets back is one of those two values.
    /// </summary>
    public static FormSource SourceOf(JsonElement parameters)
    {
        if (AcpClient.Text(parameters, "toolCallId") is { Length: > 0 })
        {
            return FormSource.Agent;
        }

        if (parameters.TryGetProperty("requestedSchema", out var schema)
            && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.EnumerateObject().Select(f => f.Name).SequenceEqual(["choice"])
            && properties.GetProperty("choice").TryGetProperty("oneOf", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.EnumerateArray().Select(c => AcpClient.Text(c, "const")).Order(StringComparer.Ordinal)
                .SequenceEqual(["cancelled", "retry_fallback"]))
        {
            return FormSource.Bridge;
        }

        return FormSource.McpServer;
    }

    /// <summary>The <c>_meta</c> key the bridge marks a question's free-text companion field with.</summary>
    public const string CustomAnswerMeta = "_askUserQuestionCustomAnswer";

    /// <summary>The form in an <c>elicitation/create</c> request, or null when it is not one the dashboard can show.</summary>
    public static QuestionForm? Parse(JsonElement parameters, string key)
    {
        if (AcpClient.Text(parameters, "mode") != "form"
            || !parameters.TryGetProperty("requestedSchema", out var schema)
            || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var message = Clip(AcpClient.Text(parameters, "message")) ?? "";
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.Object).ToList()
            : [];

        // Each question may bring an "Other" box, so twice the questions is the
        // most fields a form within the cap can have.
        if (properties.Count > MaxQuestions * 2)
        {
            return null;
        }

        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var names = properties.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        // Each question's "Other" box, by the question it belongs to.
        var others = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var field in properties)
        {
            if (OtherOf(field, names) is { } owner && !others.ContainsKey(owner))
            {
                others[owner] = field;
            }
        }

        var otherKeys = others.Values.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var questions = new List<Question>();
        foreach (var field in properties)
        {
            if (otherKeys.Contains(field.Name))
            {
                continue;
            }

            var question = Read(field, required.Contains(field.Name));
            if (question is null)
            {
                if (required.Contains(field.Name))
                {
                    return null;
                }

                continue;
            }

            if (question.Kind is QuestionKind.OneOf or QuestionKind.AnyOf && others.TryGetValue(field.Name, out var other))
            {
                question = question with { OtherKey = other.Name, OtherHint = Clip(AcpClient.Text(other.Value, "description")) };
            }

            questions.Add(question);
        }

        // An "Other" box whose question could not be shown is a question of its
        // own, rather than lost.
        foreach (var other in others.Values)
        {
            if (!questions.Any(q => q.OtherKey == other.Name) && Read(other, required.Contains(other.Name)) is { } alone)
            {
                questions.Add(alone);
            }
        }

        if (questions.Count > MaxQuestions || questions.Any(q => q.Options.Count > MaxOptions))
        {
            return null;
        }

        return questions.Count == 0 && message.Length == 0 ? null : new QuestionForm(key, message, questions, SourceOf(parameters));
    }

    /// <summary>The question a field is the free-text companion of, if it is one.</summary>
    private static string? OtherOf(JsonProperty field, HashSet<string> names)
    {
        if (AcpClient.Text(field.Value, "type") != "string")
        {
            return null;
        }

        if (field.Value.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty(CustomAnswerMeta, out var marker)
            && AcpClient.Text(marker, "questionId") is { } owner
            && names.Contains(owner))
        {
            return owner;
        }

        return null;
    }

    private static Question? Read(JsonProperty field, bool required)
    {
        var value = field.Value;
        var header = Clip(AcpClient.Text(value, "title"));
        var text = Clip(AcpClient.Text(value, "description"));
        var type = AcpClient.Text(value, "type");

        switch (type)
        {
            case "string":
                var options = Options(value, "oneOf") ?? Plain(value, "enum");
                return options is { Count: > 0 }
                    ? new Question(field.Name, header, text, QuestionKind.OneOf, options, required, Default: AcpClient.Text(value, "default"))
                    : new Question(field.Name, header, text, QuestionKind.Text, [], required,
                        Format: AcpClient.Text(value, "format"), Default: AcpClient.Text(value, "default"));

            case "array":
                var items = value.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                var picks = items.ValueKind == JsonValueKind.Object ? Options(items, "anyOf") ?? Plain(items, "enum") : null;
                if (picks is not { Count: > 0 })
                {
                    return null;
                }

                var defaults = value.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : null;
                return new Question(field.Name, header, text, QuestionKind.AnyOf, picks, required, Defaults: defaults);

            case "number" or "integer":
                var number = value.TryGetProperty("default", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetRawText()
                    : null;
                return new Question(field.Name, header, text, type == "number" ? QuestionKind.Number : QuestionKind.Integer,
                    [], required, Default: number);

            case "boolean":
                var flag = value.TryGetProperty("default", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? b.ValueKind == JsonValueKind.True ? "true" : "false"
                    : null;
                return new Question(field.Name, header, text, QuestionKind.YesNo, [], required, Default: flag);

            default:
                return null;
        }
    }

    /// <summary>Text cut to <see cref="MaxText"/>. Only what is shown is cut; option values go back as sent.</summary>
    private static string? Clip(string? text) =>
        text is { Length: > MaxText } ? text[..(MaxText - 3)] + "..." : text;

    /// <summary>Titled options: <c>{ const, title, description }</c>.</summary>
    private static List<QuestionOption>? Options(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Where(o => AcpClient.Text(o, "const") is not null)
                .Select(o => new QuestionOption(
                    AcpClient.Text(o, "const")!,
                    Clip(AcpClient.Text(o, "title") ?? AcpClient.Text(o, "const"))!,
                    Clip(AcpClient.Text(o, "description"))))
                .ToList()
            : null;

    /// <summary>Untitled options, a plain list of values.</summary>
    private static List<QuestionOption>? Plain(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => new QuestionOption(v.GetString()!, Clip(v.GetString())!, null))
                .ToList()
            : null;
}

/// <summary>
/// Your answers to a <see cref="QuestionForm"/> as you fill it in, and the
/// content they send back.
/// </summary>
/// <remarks>
/// Kept apart from the form, and by whoever shows it, because the chat redraws
/// every second: the form it is handed each time is a new copy, and what you
/// picked has to outlive that.
/// </remarks>
public sealed class QuestionDraft
{
    private readonly Dictionary<string, List<string>> _picks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _flags = new(StringComparer.Ordinal);

    public QuestionDraft(QuestionForm form)
    {
        Form = form;
        foreach (var question in form.Questions)
        {
            switch (question.Kind)
            {
                case QuestionKind.OneOf when question.Default is { } value && question.Options.Any(o => o.Value == value):
                    _picks[question.Key] = [value];
                    break;
                case QuestionKind.AnyOf when question.Defaults is { } values:
                    _picks[question.Key] = question.Options.Select(o => o.Value).Where(values.Contains).ToList();
                    break;
                case QuestionKind.Text or QuestionKind.Number or QuestionKind.Integer when question.Default is { } text:
                    _texts[question.Key] = text;
                    break;
                case QuestionKind.YesNo:
                    _flags[question.Key] = question.Default == "true";
                    break;
            }
        }
    }

    public QuestionForm Form { get; }

    public bool IsPicked(Question question, string value) =>
        _picks.TryGetValue(question.Key, out var picked) && picked.Contains(value);

    /// <summary>Picks an option. For a pick-one question it replaces the last pick.</summary>
    public void Pick(Question question, string value, bool on = true)
    {
        if (!_picks.TryGetValue(question.Key, out var picked))
        {
            picked = [];
            _picks[question.Key] = picked;
        }

        if (question.Kind == QuestionKind.OneOf)
        {
            picked.Clear();
        }

        picked.Remove(value);
        if (on)
        {
            // In the order the options are listed, not the order they were ticked.
            picked.Add(value);
            picked.Sort((a, b) => Index(question, a).CompareTo(Index(question, b)));
        }
    }

    public string Text(string key) => _texts.GetValueOrDefault(key, "");

    public void SetText(string key, string text) => _texts[key] = text;

    public bool Flag(string key) => _flags.GetValueOrDefault(key);

    public void SetFlag(string key, bool on) => _flags[key] = on;

    /// <summary>What stops the answers going back as they stand: a required question left blank, a number that is not one.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var question in Form.Questions)
        {
            var name = question.Header ?? question.Text ?? question.Key;
            var answered = Value(question, out var bad);
            if (bad)
            {
                problems.Add(question.Kind == QuestionKind.Integer ? $"{name}: a whole number, please." : $"{name}: a number, please.");
            }
            else if (question.Required && answered is null)
            {
                problems.Add($"{name}: this one needs an answer.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The answers as the form's content: only what was answered, each by its
    /// field, with picks as their values and an "Other" box under its own field.
    /// </summary>
    public IReadOnlyDictionary<string, object> Content()
    {
        var content = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var question in Form.Questions)
        {
            if (Value(question, out _) is { } value)
            {
                content[question.Key] = value;
            }

            if (question.OtherKey is { } other && Text(other).Trim() is { Length: > 0 } typed)
            {
                content[other] = typed;
            }
        }

        return content;
    }

    private object? Value(Question question, out bool bad)
    {
        bad = false;
        var text = Text(question.Key).Trim();
        switch (question.Kind)
        {
            case QuestionKind.OneOf:
                return _picks.TryGetValue(question.Key, out var one) && one.Count > 0 ? one[0] : null;

            case QuestionKind.AnyOf:
                return _picks.TryGetValue(question.Key, out var many) && many.Count > 0 ? many.ToArray() : null;

            case QuestionKind.Text:
                return text.Length > 0 ? text : null;

            case QuestionKind.Number when text.Length > 0:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return number;
                }

                bad = true;
                return null;

            case QuestionKind.Integer when text.Length > 0:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    return whole;
                }

                bad = true;
                return null;

            case QuestionKind.YesNo:
                return Flag(question.Key);

            default:
                return null;
        }
    }

    private static int Index(Question question, string value)
    {
        for (var i = 0; i < question.Options.Count; i++)
        {
            if (question.Options[i].Value == value)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
