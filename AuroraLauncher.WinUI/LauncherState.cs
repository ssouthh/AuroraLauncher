using System.Diagnostics;
using AuroraLauncher.Core;

namespace AuroraLauncher;

public static class LauncherState
{
    public static List<Instance> InstancesList { get; private set; } = Instances.LoadAll();
    public static Instance? SelectedInstance { get; set; } = InstancesList.FirstOrDefault();

    public static event Action? InstancesChanged;
    public static event Action? SelectedInstanceChanged;
    public static event Action? AccountChanged;
    public static event Action<ProgressReport>? ProgressUpdated;
    public static event Action<string, bool>? StatusChanged;
    public static event Action<CrashReportInfo, Instance?>? GameCrashed;

    public static bool IsBusy { get; private set; }

    public static void TriggerCrash(CrashReportInfo info, Instance? inst)
    {
        GameCrashed?.Invoke(info, inst);
    }

    public static void RefreshInstances()
    {
        InstancesList = Instances.LoadAll();
        if (SelectedInstance != null)
        {
            SelectedInstance = InstancesList.FirstOrDefault(i => i.Name == SelectedInstance.Name) ?? InstancesList.FirstOrDefault();
        }
        else
        {
            SelectedInstance = InstancesList.FirstOrDefault();
        }
        InstancesChanged?.Invoke();
        SelectedInstanceChanged?.Invoke();
    }

    public static void SelectInstance(Instance inst)
    {
        SelectedInstance = inst;
        SelectedInstanceChanged?.Invoke();
    }

    public static void NotifyAccountChanged()
    {
        AccountChanged?.Invoke();
    }

    public static void ReportProgress(ProgressReport report)
    {
        ProgressUpdated?.Invoke(report);
    }

    public static void SetStatus(string message, bool isBusy)
    {
        IsBusy = isBusy;
        StatusChanged?.Invoke(message, isBusy);
    }

    public static async Task<Process> LaunchAsync(Instance inst, CancellationToken ct)
    {
        SetStatus($"Запуск {inst.Name}...", true);
        try
        {
            var prog = new Progress<ProgressReport>(r =>
            {
                ReportProgress(r);
                SetStatus(string.IsNullOrWhiteSpace(r.Detail) ? r.Phase : $"{r.Phase} · {r.Detail}", true);
            });

            var proc = await GameLauncher.LaunchAsync(inst, prog, ct);
            SetStatus($"Minecraft запущен (PID {proc.Id})", false);
            return proc;
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка запуска: " + ex.Message, false);
            throw;
        }
    }
}
