using Backend.Api.Apis;
using Microsoft.Extensions.Logging.Abstractions;
namespace Backend.Api.Tests;
public sealed class SpecExtractionTests
{
 [Fact] public void ExtractsUserStoryAcceptanceCriteriaAndWbs(){var body=SpecUsEndpoints.Extract("# Checkout\n\n## User Story\n**Como** operador, **quero** publicar, **para** acompanhar.\n\n## Criterios de aceite\n- [ ] Issue criada\n\n## WBS - Plano de implementacao\n1.1 Endpoint",NullLogger.Instance);Assert.Contains("**Como** operador",body);Assert.Contains("- [ ] Issue criada",body);Assert.Contains("1.1 Endpoint",body);}
 [Fact] public void MissingSectionProducesEmptySectionWithoutThrowing(){var body=SpecUsEndpoints.Extract("# Only title\n\n## User Story\nStory",NullLogger.Instance);Assert.Contains("## User Story\nStory",body);Assert.Contains("## Criterios de aceite\n\n",body);}
 [Fact] public void ConvertsStructuredMarkdownToBasicHtml(){var html=SpecUsEndpoints.Html("## Criterios\n- **feito**\nTexto");Assert.Contains("<h2>Criterios</h2>",html);Assert.Contains("<strong>feito</strong>",html);Assert.Contains("<li>",html);}
}
