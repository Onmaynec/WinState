using System.Text;
using WinState.Core.Profiles;

Console.OutputEncoding = Encoding.UTF8;
return await WinStateCli.RunAsync(args, CancellationToken.None);

internal static class WinStateCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "--version":
            case "version":
                Console.WriteLine("WinState 0.1.0-alpha.1");
                return 0;
            case "architecture":
                PrintArchitecture();
                return 0;
            case "validate":
                return await ValidateAsync(args, cancellationToken);
            default:
                Console.Error.WriteLine($"[ERROR] Неизвестная команда: {args[0]}");
                Console.Error.WriteLine("Выполните: winstate --help");
                return 2;
        }
    }

    private static async Task<int> ValidateAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: winstate validate <путь-к-winstate.yaml>");
            return 2;
        }

        try
        {
            var reader = new BootstrapYamlProfileReader();
            var profile = await reader.LoadAsync(args[1], cancellationToken);
            var result = new ProfileValidator().Validate(profile);

            Console.WriteLine($"Профиль: {profile.Metadata.Name}");
            Console.WriteLine($"Schema:  {profile.SchemaVersion}");
            Console.WriteLine($"User environment:    {profile.Environment.User.Count}");
            Console.WriteLine($"Machine environment: {profile.Environment.Machine.Count}");

            if (result.IsValid)
            {
                Console.WriteLine("[OK] Базовая структура профиля корректна.");
                return 0;
            }

            foreach (var issue in result.Issues)
                Console.Error.WriteLine($"[ERROR] {issue.Path}: {issue.Message} ({issue.Code})");

            return 3;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"[ERROR] {exception.Message}");
            return 4;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        WINSTATE
        Git для конфигурации Windows.

        Текущий этап: архитектурное ядро 0.1.0-alpha.1

        Команды:
          winstate --help                  Показать справку
          winstate --version               Показать версию
          winstate architecture            Показать границы модулей
          winstate validate <profile>      Проверить bootstrap-структуру YAML

        Следующий этап:
          полный Profile Engine → Environment Provider vertical slice
        """);
    }

    private static void PrintArchitecture()
    {
        Console.WriteLine("""
        Profile → Validation → Discovery → Diff → Plan → Confirmation
                    ↓                                  ↓
                 Diagnostics                    Checkpoint → Apply
                                                       ↓
                                               Verify → Transaction
                                                       ↓
                                                    Rollback

        Domain: без Windows, SQLite, YAML и CLI
        Core:   движки профилей и планирования
        CLI:    только ввод, вывод и exit codes
        """);
    }
}
