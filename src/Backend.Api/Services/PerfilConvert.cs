using Backend.Persistence.Domain;

namespace Backend.Api.Services;

/// <summary>Shared string &lt;-&gt; Perfil mapping (snake_case wire format, seção 3/8) so CredentialEndpoints and WebhookEndpoints don't each keep their own copy.</summary>
internal static class PerfilConvert
{
    public static Perfil? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "analista_requisitos" => Perfil.AnalistaRequisitos,
        "arquiteto" => Perfil.Arquiteto,
        "dev" => Perfil.Dev,
        "revisor" => Perfil.Revisor,
        "qa" => Perfil.Qa,
        "seguranca" => Perfil.Seguranca,
        "release_deploy" => Perfil.ReleaseDeploy,
        _ => null
    };

    public static string ToApiString(Perfil perfil) => perfil switch
    {
        Perfil.AnalistaRequisitos => "analista_requisitos",
        Perfil.Arquiteto => "arquiteto",
        Perfil.Dev => "dev",
        Perfil.Revisor => "revisor",
        Perfil.Qa => "qa",
        Perfil.Seguranca => "seguranca",
        Perfil.ReleaseDeploy => "release_deploy",
        _ => perfil.ToString()
    };
}
