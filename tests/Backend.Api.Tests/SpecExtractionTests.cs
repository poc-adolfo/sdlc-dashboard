using Backend.Api.Apis;
using Microsoft.Extensions.Logging.Abstractions;
namespace Backend.Api.Tests;
public sealed class SpecExtractionTests
{
 [Fact] public void ExtractsUserStoryAcceptanceCriteriaAndWbs(){var body=SpecUsEndpoints.Extract("# Checkout\n\n## User Story\n**Como** operador, **quero** publicar, **para** acompanhar.\n\n## Criterios de aceite\n- [ ] Issue criada\n\n## WBS - Plano de implementacao\n1.1 Endpoint",NullLogger.Instance);Assert.Contains("**Como** operador",body);Assert.Contains("- [ ] Issue criada",body);Assert.Contains("1.1 Endpoint",body);}
 [Fact] public void MissingSectionProducesEmptySectionWithoutThrowing(){var body=SpecUsEndpoints.Extract("# Only title\n\n## User Story\nStory",NullLogger.Instance);Assert.Contains("## User Story\nStory",body);Assert.Contains("## Criterios de aceite\n\n",body);}

 [Fact]
 public void ProducesExactSectionOrderAndFormatWhenAllSectionsPresent()
 {
     var body = SpecUsEndpoints.Extract("# Checkout\n\n## User Story\nStory text\n\n## Criterios de aceite\n- ok\n\n## WBS - Plano de implementacao\n1.1 Item", NullLogger.Instance);
     Assert.Equal("## User Story\nStory text\n\n## Criterios de aceite\n- ok\n\n## WBS - Plano de implementacao\n1.1 Item", body);
 }

 [Fact]
 public void AllThreeSectionsMissingProducesEmptyBodiesWithoutThrowing()
 {
     var body = SpecUsEndpoints.Extract("# Only a title, no sections at all", NullLogger.Instance);
     Assert.Equal("## User Story\n\n\n## Criterios de aceite\n\n\n## WBS - Plano de implementacao\n", body);
 }

 [Fact]
 public void WbsHeadingWithoutSuffixTextIsStillMatched()
 {
     var body = SpecUsEndpoints.Extract("## WBS\n1. Primeiro item", NullLogger.Instance);
     Assert.Contains("1. Primeiro item", body);
 }

 [Fact]
 public void SectionsOutOfOrderInSourceAreStillExtractedIntoFixedOutputOrder()
 {
     var body = SpecUsEndpoints.Extract("## WBS - Plano\nW\n\n## Criterios de aceite\nC\n\n## User Story\nU", NullLogger.Instance);
     var userIndex = body.IndexOf("## User Story", StringComparison.Ordinal);
     var criteriaIndex = body.IndexOf("## Criterios de aceite", StringComparison.Ordinal);
     var wbsIndex = body.IndexOf("## WBS", StringComparison.Ordinal);
     Assert.True(userIndex < criteriaIndex && criteriaIndex < wbsIndex);
     Assert.Contains("\nU", body);
     Assert.Contains("\nC", body);
     Assert.Contains("\nW", body);
 }

 [Fact] public void ConvertsStructuredMarkdownToBasicHtml(){var html=SpecUsEndpoints.Html("## Criterios\n- **feito**\nTexto");Assert.Contains("<h2>Criterios</h2>",html);Assert.Contains("<strong>feito</strong>",html);Assert.Contains("<li>",html);}

 [Fact]
 public void HtmlEncodesAngleBracketsAndAmpersandsInHeadingsListsAndParagraphs()
 {
     var html = SpecUsEndpoints.Html("## <script>alert(1)</script>\n- <img src=x onerror=alert(1)>\nA & B < C");
     Assert.DoesNotContain("<script>", html);
     Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
     Assert.DoesNotContain("<img", html);
     Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
     Assert.Contains("A &amp; B &lt; C", html);
 }

 [Fact]
 public void HtmlBoldMarkersInsideListItemsAreConvertedAfterEncoding()
 {
     var html = SpecUsEndpoints.Html("- **<b>nao deveria virar negrito html cru</b>**");
     Assert.Contains("<strong>&lt;b&gt;nao deveria virar negrito html cru&lt;/b&gt;</strong>", html);
     Assert.DoesNotContain("<b>nao deveria", html);
 }

 [Fact]
 public void HtmlSkipsBlankLinesAndProducesNoEmptyParagraphs()
 {
     var html = SpecUsEndpoints.Html("## Titulo\n\n\n- item\n\nTexto final");
     Assert.DoesNotContain("<p></p>", html);
     Assert.Equal("<h2>Titulo</h2><li>item</li><p>Texto final</p>", html);
 }
}
