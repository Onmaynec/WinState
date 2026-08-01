using System.Globalization;
using WinState.Domain.Configuration;
using WinState.Domain.Errors;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;

namespace WinState.Core.Profiles
{
    public sealed record ProfileValidationIssue(string Code, string Message, string Path);
    public sealed record ProfileValidationResult(IReadOnlyCollection<ProfileValidationIssue> Issues) { public bool IsValid => Issues.Count == 0; }

    public sealed class ProfileValidator
    {
        public ProfileValidationResult Validate(WinStateProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var issues = new List<ProfileValidationIssue>();
            if (profile.SchemaVersion != 1) issues.Add(new("profile.schema.unsupported", "Поддерживается только schemaVersion: 1.", "schemaVersion"));
            if (string.IsNullOrWhiteSpace(profile.Metadata.Name)) issues.Add(new("profile.metadata.name.required", "Укажите metadata.name.", "metadata.name"));
            ValidateVariables(profile.Environment.User, "environment.user", issues);
            ValidateVariables(profile.Environment.Machine, "environment.machine", issues);
            return new(issues);
        }

        private static void ValidateVariables(IReadOnlyDictionary<string, string> variables, string path, ICollection<ProfileValidationIssue> issues)
        {
            foreach (var pair in variables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) issues.Add(new("environment.name.required", "Имя переменной не может быть пустым.", path));
                if (pair.Key.Contains('=')) issues.Add(new("environment.name.invalid", "Имя переменной не должно содержать '='.", $"{path}.{pair.Key}"));
            }
        }
    }

    /// <summary>Безопасный bootstrap-reader заголовка профиля и environment-секции.</summary>
    public sealed class BootstrapYamlProfileReader
    {
        public async Task<WinStateProfile> LoadAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Путь к профилю не указан.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Профиль не найден.", path);

            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var schemaVersion = 0;
            string? name = null;
            string? description = null;
            var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var machine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? topSection = null;
            string? environmentScope = null;

            foreach (var rawLine in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = StripComment(rawLine);
                if (string.IsNullOrWhiteSpace(content)) continue;
                var indent = content.TakeWhile(char.IsWhiteSpace).Count();
                var trimmed = content.Trim();

                if (indent == 0)
                {
                    environmentScope = null;
                    if (TrySplit(trimmed, out var key, out var value))
                    {
                        topSection = key;
                        if (key.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
                            _ = int.TryParse(Unquote(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out schemaVersion);
                    }
                    continue;
                }

                if (topSection?.Equals("metadata", StringComparison.OrdinalIgnoreCase) == true && indent >= 2 && TrySplit(trimmed, out var metadataKey, out var metadataValue))
                {
                    if (metadataKey.Equals("name", StringComparison.OrdinalIgnoreCase)) name = Unquote(metadataValue);
                    else if (metadataKey.Equals("description", StringComparison.OrdinalIgnoreCase)) description = Unquote(metadataValue);
                    continue;
                }

                if (topSection?.Equals("environment", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (indent == 2 && trimmed.EndsWith(':')) { environmentScope = trimmed.TrimEnd(':'); continue; }
                    if (indent >= 4 && TrySplit(trimmed, out var variableName, out var variableValue))
                    {
                        var target = environmentScope?.Equals("machine", StringComparison.OrdinalIgnoreCase) == true ? machine :
                            environmentScope?.Equals("user", StringComparison.OrdinalIgnoreCase) == true ? user : null;
                        target?.TryAdd(variableName, Unquote(variableValue));
                    }
                }
            }

            return new WinStateProfile
            {
                SchemaVersion = schemaVersion,
                Metadata = new ProfileMetadata { Name = name ?? string.Empty, Description = description },
                Environment = new EnvironmentProfileSection { User = user, Machine = machine }
            };
        }

        private static string StripComment(string line)
        {
            var inSingle = false;
            var inDouble = false;
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (character == '\'' && !inDouble) inSingle = !inSingle;
                else if (character == '"' && !inSingle) inDouble = !inDouble;
                else if (character == '#' && !inSingle && !inDouble) return line[..index];
            }
            return line;
        }

        private static bool TrySplit(string line, out string key, out string value)
        {
            var separator = line.IndexOf(':');
            if (separator < 0) { key = string.Empty; value = string.Empty; return false; }
            key = line[..separator].Trim();
            value = line[(separator + 1)..].Trim();
            return true;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
                return value[1..^1].Replace("\\\\", "\\", StringComparison.Ordinal);
            return value;
        }
    }
}

namespace WinState.Core.Planning
{
    public sealed class DependencyGraph
    {
        public IReadOnlyList<PlannedAction> Sort(IReadOnlyCollection<PlannedAction> actions)
        {
            ArgumentNullException.ThrowIfNull(actions);
            var byId = new Dictionary<string, PlannedAction>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions)
                if (!byId.TryAdd(action.Id, action)) throw new WinStateDomainException($"Действие с ID '{action.Id}' объявлено несколько раз.");

            var incoming = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
            var dependents = byId.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions)
            {
                foreach (var dependency in action.DependsOn.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!byId.ContainsKey(dependency)) throw new WinStateDomainException($"Действие '{action.Id}' зависит от неизвестного действия '{dependency}'.");
                    incoming[action.Id]++;
                    dependents[dependency].Add(action.Id);
                }
            }

            var ready = new SortedSet<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
            var result = new List<PlannedAction>(actions.Count);
            while (ready.Count > 0)
            {
                var id = ready.Min!;
                ready.Remove(id);
                result.Add(byId[id]);
                foreach (var dependent in dependents[id].OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    incoming[dependent]--;
                    if (incoming[dependent] == 0) ready.Add(dependent);
                }
            }

            if (result.Count != actions.Count)
            {
                var cycle = incoming.Where(pair => pair.Value > 0).Select(pair => pair.Key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
                throw new WinStateDomainException($"Обнаружен цикл зависимостей: {string.Join(" -> ", cycle)}.");
            }
            return result;
        }
    }

    public sealed record PlanSummary(int Total, int Changes, int Destructive, int AdministratorActions, int RebootActions, RiskLevel MaximumRisk)
    {
        public static PlanSummary From(IReadOnlyCollection<PlannedAction> actions)
        {
            ArgumentNullException.ThrowIfNull(actions);
            var changes = actions.Where(action => action.Operation != ActionType.NoOp).ToArray();
            var destructive = changes.Count(action => action.Operation is ActionType.Remove or ActionType.Uninstall or ActionType.Disable);
            var maxRisk = actions.Select(action => action.Risk).DefaultIfEmpty(RiskLevel.None).Max();
            return new(actions.Count, changes.Length, destructive, changes.Count(action => action.RequiresAdministrator), changes.Count(action => action.MayRequireReboot), maxRisk);
        }
    }
}
