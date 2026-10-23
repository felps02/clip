using ClipForge.Api.Features.Assistant;

namespace ClipForge.Api.Tests.Unit;

/// <summary>Which mentions of the user's message are negated (the rule layer never applies those).</summary>
public class AssistantNegationTests
{
    private static bool IsNegated(string message, string mention)
    {
        var text = new FoldedText(message);
        var index = text.Folded.IndexOf(FoldedText.Fold(mention), StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, $"'{mention}' is not in '{message}'");
        return AssistantNegation.IsNegated(text, index, mention.Length);
    }

    [Theory]
    [InlineData("não quero webcam", "webcam")]
    [InlineData("quero sem o layout de webcam", "webcam")]
    [InlineData("nada de legenda no topo", "legenda")]
    [InlineData("não quero legenda no meio, quero embaixo", "no meio")]
    [InlineData("a legenda não pode ficar no meio", "no meio")]
    [InlineData("não quero a legenda em cima", "em cima")]
    [InlineData("não quero que deixe a legenda no meio", "no meio")]
    [InlineData("a live não é em inglês", "inglês")]
    [InlineData("a live nao e em ingles", "ingles")]
    [InlineData("não quero clipes curtos", "clipes curtos")]
    [InlineData("evita clipes curtos", "clipes curtos")]
    [InlineData("nada de clipes longos", "clipes longos")]
    [InlineData("sem clipes longos", "clipes longos")]
    [InlineData("não quero mais sensível", "mais sensível")]
    [InlineData("clipes curtos não", "clipes curtos")]
    [InlineData("a webcam não precisa aparecer", "webcam")]
    [InlineData("nem muito curtos", "curtos")]
    [InlineData("não me coloca legenda em cima", "em cima")]
    [InlineData("nenhuma legenda no meio", "no meio")]
    [InlineData("não quero perder tempo com clipes longos", "clipes longos")]
    public void Mentions_under_a_negation(string message, string mention) => IsNegated(message, mention).ShouldBeTrue();

    [Theory]
    [InlineData("sem legenda e engraçados", "engraçados")]
    [InlineData("sem legenda engraçados", "engraçados")]
    [InlineData("sem legenda webcam em cima", "webcam")]
    [InlineData("não quero legenda no meio, quero embaixo", "embaixo")]
    [InlineData("legenda embaixo e não no meio", "embaixo")]
    [InlineData("não sei mexer nisso coloca a legenda em cima", "em cima")]
    [InlineData("não quero mudar nada só a legenda no meio", "no meio")]
    [InlineData("sem webcam legenda no meio", "no meio")]
    [InlineData("clipes de mais ou menos 30 segundos", "30 segundos")]
    [InlineData("não só engraçados", "engraçados")]
    [InlineData("não esquece de colocar legenda", "colocar legenda")]
    [InlineData("por que não clipes curtos?", "clipes curtos")]
    [InlineData("não dá pra colocar a legenda em cima?", "em cima")]
    [InlineData("quero clipes curtos, não longos", "clipes curtos")]
    [InlineData("não é em inglês, é em português", "português")]
    [InlineData("não quero mais de 10 clipes", "10 clipes")]
    [InlineData("a live é em inglês", "inglês")]
    [InlineData("não quero perder nenhuma jogada", "jogada")]
    [InlineData("não pode faltar legenda", "legenda")]
    [InlineData("não tem problema ser em inglês", "inglês")]
    public void Mentions_outside_a_negation(string message, string mention) => IsNegated(message, mention).ShouldBeFalse();
}
