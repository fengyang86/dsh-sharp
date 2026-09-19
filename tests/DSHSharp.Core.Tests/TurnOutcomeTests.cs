using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class TurnOutcomeTests
{
    private static string PageJson(params string[] records) =>
        "{\"result\":{\"ok\":true,\"value\":{\"records\":[" + string.Join(",", records) + "]}}}";

    private static string TurnEnd(string kind, string? error = null) =>
        "{\"type\":\"event\",\"event\":{\"type\":\"turn/end\",\"data\":{\"turn\":3,\"reason\":{" +
        (error is null
            ? $"\"kind\":\"{kind}\""
            : $"\"kind\":\"{kind}\",\"error\":{{\"message\":\"{error}\"}}") + "}}}}";

    private const string AssistantMessage =
        "{\"type\":\"event\",\"event\":{\"type\":\"assistant/message\",\"data\":{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"答复文本\"}]}}}}";

    [Fact]
    public void Parses_CompletedTurn_WithAssistantText()
    {
        var outcome = DshRpcParser.ParseTurnOutcome(
            PageJson(TurnEnd("completed"), AssistantMessage));

        Assert.NotNull(outcome);
        Assert.Equal("completed", outcome!.Kind);
        Assert.Null(outcome.ErrorMessage);
        Assert.Equal("答复文本", outcome.LastAssistantText);
    }

    [Fact]
    public void Parses_ErrorTurn_WithMessage()
    {
        var outcome = DshRpcParser.ParseTurnOutcome(
            PageJson(TurnEnd("error", "模型请求失败：429"), AssistantMessage));

        Assert.NotNull(outcome);
        Assert.Equal("error", outcome!.Kind);
        Assert.Equal("模型请求失败：429", outcome.ErrorMessage);
        Assert.Equal("答复文本", outcome.LastAssistantText);
    }

    [Fact]
    public void Parses_AbortedTurn()
    {
        var outcome = DshRpcParser.ParseTurnOutcome(PageJson(TurnEnd("aborted")));

        Assert.NotNull(outcome);
        Assert.Equal("aborted", outcome!.Kind);
        Assert.Null(outcome.LastAssistantText);
    }

    [Fact]
    public void MissingTurnEnd_ReturnsNullKindInsideOutcome()
    {
        var outcome = DshRpcParser.ParseTurnOutcome(PageJson(AssistantMessage));

        Assert.NotNull(outcome);
        Assert.Null(outcome!.Kind);
        Assert.Equal("答复文本", outcome.LastAssistantText);
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        Assert.Null(DshRpcParser.ParseTurnOutcome("{not json"));
        Assert.Null(DshRpcParser.ParseTurnOutcome("{\"result\":{\"ok\":false}}"));
    }
}
