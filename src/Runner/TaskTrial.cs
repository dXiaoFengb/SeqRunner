using System.IO;

namespace Runner;

internal static class TaskTrial
{
    internal sealed record Result(Task? Running, string? Error);
    internal static Result Start(Store store, RunnerEngine engine, string taskId, Func<bool> startupBusy, Func<bool> cancelCompletion)
    {
        if (engine.Running || engine.NeedsRecovery || startupBusy()) return new(null, "当前有运行或待核实事项，不能试运行。");
        var task = store.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return new(null, "该任务尚未保存，请先保存配置。");
        var review = TaskConfigurationReview.Check(task);
        if (!review.CanRun) return new(null, review.Describe());
        if (!cancelCompletion()) return new(null, "无法取消待处理的完成后操作，请查看运行状态。");
        var run = new RunRecord { Trigger = "手动试运行", PlanName = task.Name, State = "运行中" };
        run.Events.Add($"{DateTimeOffset.Now:O} 已保存单任务试运行意图");
        store.History.Add(run);
        try { store.Save(); }
        catch (Exception ex) { store.History.Remove(run); return new(null, "试运行记录保存失败，未启动：" + ex.Message); }
        return new(engine.StartTasks([task.Copy()], run), null);
    }
}
