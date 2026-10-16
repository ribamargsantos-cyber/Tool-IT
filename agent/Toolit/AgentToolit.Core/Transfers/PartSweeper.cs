using System.Text.RegularExpressions;
using AgentToolit.Core.Config;
using Microsoft.Extensions.Logging;

namespace AgentToolit.Core.Transfers;

/// <summary>
/// Remove <c>.toolit-{uuid}.part</c> órfãos das raízes de ENVIO (ADR-023, L49). Só apaga nome no padrão estrito (UUID
/// canônico), arquivo comum (links/reparse points e pastas-link não são seguidos) com <c>LastWriteTime</c> mais antigo
/// que <see cref="AgentSettings.TransferPartRetentionHours"/>; a remoção é PELO HANDLE (<see cref="PathGuard.OpenChecked"/>
/// + <see cref="PathGuard.DeleteByHandle"/>, nunca por caminho). A abertura é exclusiva: um .part em uso por uma
/// transferência em andamento não abre e é pulado. Nunca lança; log só do nome.
/// </summary>
public sealed partial class PartSweeper(TransferRootRegistry registry, AgentSettings settings, TimeProvider time, ILogger<PartSweeper> logger)
{
    [GeneratedRegex(@"^\.toolit-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.part$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    public static bool IsPartName(string name) => PartName().IsMatch(name);

    /// <summary>Uma varredura; devolve quantos .part foram apagados.</summary>
    public int Sweep()
    {
        var removed = 0;
        var limit = time.GetUtcNow() - TimeSpan.FromHours(settings.TransferPartRetentionHours);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,   // não segue link/junction
        };
        foreach (var root in registry.SendRoots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(root, ".toolit-*.part", options))
                {
                    var name = Path.GetFileName(path);
                    if (!IsPartName(name) || new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) > limit)
                    {
                        continue;
                    }

                    if (TryDelete(path, name))
                    {
                        removed++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Varredura de .part: raiz ignorada.");
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Varredura de .part órfãos: {Count} arquivo(s) removido(s).", removed);
        }

        return removed;
    }

    private bool TryDelete(string path, string name)
    {
        using var part = PathGuard.OpenChecked(path, FileOpenIntent.AppendExisting, registry.SendRoots, out var code);
        if (part is null)
        {
            if (code is not ("not_found" or "open_failed"))
            {
                logger.LogWarning(".part órfão {Name} não removido ({Code}).", name, code);
            }

            return false;   // open_failed: em uso (transferência ativa) → pula
        }

        return PathGuard.DeleteByHandle(part.SafeFileHandle);
    }
}
