using WinState.Domain.Configuration;
using WinState.Domain.Errors;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;

namespace WinState.Core.Profiles
{
    public sealed record ProfileValidationIssue(string Code, string Message, string Path);
    public sealed record ProfileValidationResult(IReadOnlyCollection<ProfileValidationIssue> Issues)
    {
        public bool IsValid => Issues.Count == 0;
    }

    public sealed class ProfileValidator
    {
        private static readonly HashSet<string> SupportedPathStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "present", "absent"
        };

        private static readonly HashSet<string> SupportedPathPositions = new(StringComparer.OrdinalIgnoreCase)
        {
            "prepend", "append"
        };

        public ProfileValidationResult Validate(WinStateProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var issues = new List<ProfileValidationIssue>();
            if (profile.SchemaVersion != 1)
            {
                issues.Add(new("profile.schema.unsupported", "Поддерживается только schemaVersion: 1.", "schemaVersion"));
            }

            if (string.IsNullOrWhiteSpace(profile.Metadata.Name))
            {
                issues.Add(new("profile.metadata.name.required", "Укажите metadata.name.", "metadata.name"));
            }

            ValidateVariables(profile.Environment.User, "environment.user", issues);
            ValidateVariables(profile.Environment.Machine, "environment.machine", issues);
            ValidatePath(profile.Environment.UserPath, "environment.userPath", issues);
            ValidatePath(profile.Environment.MachinePath, "environment.machinePath", issues);
            return new ProfileValidationResult(issues);
        }

        private static void ValidateVariables(
            IReadOnlyDictionary<string, string> variables,
            string path,
            ICollection<ProfileValidationIssue> issues)
        {
            foreach (var pair in variables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    issues.Add(new("environment.name.required", "Имя переменной не может быть пустым.", path));
                }

                if (pair.Key.Contains('='))
                {
                    issues.Add(new("environment.name.invalid", "Имя переменной не должно содержать '='.", $"{path}.{pair.Key}"));
                }
            }
        }

        private static void ValidatePath(
            IReadOnlyCollection<PathEntryProfile> entries,
            string path,
            ICollection<ProfileValidationIssue> issues)
        {
            var index = 0;
            foreach (var entry in entries)
            {
                var entryPath = $"{path}[{index}]";
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    issues.Add(new("environment.path.required", "Путь не может быть пустым.", $"{entryPath}.path"));
                }

                if (!SupportedPathStates.Contains(entry.State))
                {
                    issues.Add(new("environment.path.state.unsupported", "Поддерживаются состояния present и absent.", $"{entryPath}.state"));
                }

                if (!SupportedPathPositions.Contains(entry.Position))
                {
                    issues.Add(new("environment.path.position.unsupported", "Поддерживаются позиции prepend и append.", $"{entryPath}.position"));
                }

                index++;
            }
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
            {
                if (!byId.TryAdd(action.Id, action))
                {
                    throw new WinStateDomainException($"Действие с ID '{action.Id}' объявлено несколько раз.");
                }
            }

            var incoming = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
            var dependents = byId.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions)
            {
                foreach (var dependency in action.DependsOn.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!byId.ContainsKey(dependency))
                    {
                        throw new WinStateDomainException($"Действие '{action.Id}' зависит от неизвестного действия '{dependency}'.");
                    }

                    incoming[action.Id]++;
                    dependents[dependency].Add(action.Id);
                }
            }

            var ready = new SortedSet<string>(
                incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key),
                StringComparer.OrdinalIgnoreCase);
            var result = new List<PlannedAction>(actions.Count);
            while (ready.Count > 0)
            {
                var id = ready.Min!;
                ready.Remove(id);
                result.Add(byId[id]);
                foreach (var dependent in dependents[id].OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    incoming[dependent]--;
                    if (incoming[dependent] == 0)
                    {
                        ready.Add(dependent);
                    }
                }
            }

            if (result.Count != actions.Count)
            {
                var cycle = incoming
                    .Where(pair => pair.Value > 0)
                    .Select(pair => pair.Key)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
                throw new WinStateDomainException($"Обнаружен цикл зависимостей: {string.Join(" -> ", cycle)}.");
            }

            return result;
        }
    }

    public sealed record PlanSummary(
        int Total,
        int Changes,
        int Destructive,
        int AdministratorActions,
        int RebootActions,
        RiskLevel MaximumRisk)
    {
        public static PlanSummary From(IReadOnlyCollection<PlannedAction> actions)
        {
            ArgumentNullException.ThrowIfNull(actions);
            var changes = actions.Where(action => action.Operation != ActionType.NoOp).ToArray();
            var destructive = changes.Count(action => action.Operation is ActionType.Remove or ActionType.Uninstall or ActionType.Disable);
            var maxRisk = actions.Select(action => action.Risk).DefaultIfEmpty(RiskLevel.None).Max();
            return new PlanSummary(
                actions.Count,
                changes.Length,
                destructive,
                changes.Count(action => action.RequiresAdministrator),
                changes.Count(action => action.MayRequireReboot),
                maxRisk);
        }
    }
}
