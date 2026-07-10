using System.Text;
using System.Text.RegularExpressions;

namespace AzureMoe.Chat.Core;

/// <summary>
/// Normalises Azure/Microsoft service names so H2-derived names (Update posts)
/// and LLM-extracted names (articles) land on the same AzureService node.
/// NFKC + whitespace collapse + a small curated alias map. Deliberately does
/// NOT auto-prefix "Azure" — that would break "Microsoft Entra ID",
/// "GitHub Actions" and friends.
/// </summary>
public static partial class ServiceNames
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Common shorthands → canonical names. Keys are matched case-insensitively
    // against the NFKC-normalised input. Extend from reality:
    //   inspect <db> --cypher "MATCH (s:AzureService) RETURN s.name ORDER BY s.name"
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["App Service"]            = "Azure App Service",
        ["Functions"]              = "Azure Functions",
        ["AKS"]                    = "Azure Kubernetes Service",
        ["Kubernetes Service"]     = "Azure Kubernetes Service",
        ["ACA"]                    = "Azure Container Apps",
        ["Container Apps"]         = "Azure Container Apps",
        ["ACR"]                    = "Azure Container Registry",
        ["Container Registry"]     = "Azure Container Registry",
        ["APIM"]                   = "Azure API Management",
        ["API Management"]         = "Azure API Management",
        ["Cosmos DB"]              = "Azure Cosmos DB",
        ["SQL Database"]           = "Azure SQL Database",
        ["Key Vault"]              = "Azure Key Vault",
        ["Service Bus"]            = "Azure Service Bus",
        ["Event Grid"]             = "Azure Event Grid",
        ["Event Hubs"]             = "Azure Event Hubs",
        ["Virtual Machines"]       = "Azure Virtual Machines",
        ["Data Factory"]           = "Azure Data Factory",
        ["DevOps"]                 = "Azure DevOps",
        ["Azure OpenAI"]           = "Azure OpenAI Service",
        ["OpenAI Service"]         = "Azure OpenAI Service",
        ["Entra"]                  = "Microsoft Entra ID",
        ["Entra ID"]               = "Microsoft Entra ID",
        ["Azure AD"]               = "Microsoft Entra ID",
        ["Azure Active Directory"] = "Microsoft Entra ID",
        ["Defender for Cloud"]     = "Microsoft Defender for Cloud",
    };

    /// <summary>Normalise a raw service name; returns "" for blank input.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Normalize(NormalizationForm.FormKC).Trim();
        s = Whitespace().Replace(s, " ");
        return Aliases.TryGetValue(s, out var canonical) ? canonical : s;
    }
}
