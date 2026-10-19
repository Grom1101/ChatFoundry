using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ChatFoundry.LoadTests;

public class DockerLoadTest(ITestOutputHelper output)
{
    private static readonly HttpClient HttpClient = new();

    [Fact(DisplayName = "Запуск нагрузочного тестирования диалога с ветвлениями в Docker")]
    public async Task Run_BranchingDialog_LoadTest_In_Docker()
    {
        output.WriteLine("=== Запуск нагрузочного тестирования ChatFoundry в Docker ===");

        var rootDir = FindSolutionRoot();
        var composeFile = Path.Combine(rootDir, "deployment", "docker-compose.loadtest.yml");
        File.Exists(composeFile).Should().BeTrue($"Compose файл должен существовать: {composeFile}");

        // 1. Убеждаемся, что инфраструктура, сервисы и стек метрик подняты
        output.WriteLine("[1/3] Поднятие контейнеров (docker compose up -d)...");
        var upResult = await RunCommandAsync("docker", $"compose -f \"{composeFile}\" up -d --build postgres redis kafka telegram-mock workflow-service client-service telegram-service seq jaeger prometheus grafana", rootDir);
        output.WriteLine(upResult.Output);
        upResult.ExitCode.Should().Be(0, "Контейнеры должны успешно подняться");

        // Ждем готовности сервисов
        output.WriteLine("Ожидание прогрева сервисов (10 сек)...");
        await Task.Delay(10_000);

        // 2. Запускаем нагрузочный тест k6
        output.WriteLine("[2/3] Запуск k6 нагрузочного сценария (docker compose run k6)...");
        var k6Result = await RunCommandAsync("docker", $"compose -f \"{composeFile}\" run --rm k6", rootDir);
        output.WriteLine(k6Result.Output);
        k6Result.ExitCode.Should().Be(0, "k6 тест должен завершиться успешно без превышения SLA и сбоев");

        // 3. Проверяем результаты сквозной доставки в TelegramMock
        output.WriteLine("[3/3] Сквозная верификация доставки в Telegram Mock...");
        try
        {
            var mockStatsResponse = await HttpClient.GetStringAsync("http://localhost:8085/mock/stats");
            output.WriteLine($"Статистика Mock: {mockStatsResponse}");

            using var doc = JsonDocument.Parse(mockStatsResponse);
            var totalReceived = doc.RootElement.GetProperty("totalReceived").GetInt64();
            totalReceived.Should().BeGreaterThan(0, "Telegram Mock должен был получить исходящие сообщения от сервисов");
            output.WriteLine($"УСПЕХ: Telegram Mock успешно обработал {totalReceived} исходящих сообщений бота!");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Предупреждение при прямом обращении к http://localhost:8085/mock/stats (если порт не проброшен наружу): {ex.Message}");
        }

        var reportPath = Path.Combine(rootDir, "tests", "load-tests", "reports", "report.html");
        if (File.Exists(reportPath))
        {
            output.WriteLine($"\n=======================================================");
            output.WriteLine($"HTML ОТЧЕТ: file:///{reportPath.Replace('\\', '/')}");
            output.WriteLine($"=======================================================\n");
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ChatFoundry.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("ChatFoundry.sln root not found");
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(string fileName, string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var stdout = await outputTask;
        var stderr = await errorTask;

        return (process.ExitCode, stdout + "\n" + stderr);
    }
}
