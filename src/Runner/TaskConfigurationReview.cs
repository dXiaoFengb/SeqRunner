using System.IO;

namespace Runner;

internal sealed record TaskReviewResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    internal bool CanRun => Errors.Count == 0;
    internal string Describe() => (Errors.Count == 0 ? "配置检查：没有发现必填项错误。" : "请先修改：\n" + string.Join("\n", Errors.Select(x => "• " + x))) + "\n\n需要人工确认：\n" + string.Join("\n", Warnings.Select(x => "• " + x));
}

internal static class TaskConfigurationReview
{
    internal static TaskReviewResult Check(TaskSpec task)
    {
        var errors = new List<string>(); var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(task.Program) || !File.Exists(task.Program)) errors.Add("程序路径为空或文件不存在。");
        if (!string.IsNullOrWhiteSpace(task.WorkingDirectory) && !Directory.Exists(task.WorkingDirectory)) errors.Add("工作目录不存在。");
        if (task.Completion is not ("进程退出" or "日志信号" or "人工确认" or "MAA日志")) errors.Add("完成方式无效。");
        if (task.LogSource is not ("程序输出" or "日志文件")) errors.Add("日志来源无效。");
        if (task.Completion == "日志信号" && (string.IsNullOrWhiteSpace(task.SuccessText) || task.SuccessText.Length > 4096)) errors.Add("日志成功文字不能为空且不能超过 4096 字符。");
        if ((task.Completion == "MAA日志" || task.Completion == "日志信号" && task.LogSource == "日志文件") && (string.IsNullOrWhiteSpace(task.LogPath) || !File.Exists(task.LogPath))) errors.Add("所选日志文件不存在，运行前需要已有日志文件。");
        if (task.Completion == "MAA日志" && (!task.MaaExclusiveLog || task.SharedLog)) errors.Add("MAA 日志必须明确确认不共享。");
        if (task.MaaStartByRunner && task.Completion != "MAA日志") errors.Add("由 V3 启动 MAA 任务需要选择 MAA日志。");
        if (task.TimeoutMinutes < 0 || task.TimeoutMinutes > 525600) errors.Add("最长运行时间应为 0 至 525600 分钟。");
        if (!UpdateMarkers.TryParse(task.UpdateMarkers, out _)) errors.Add("更新或重启线索最多 32 条，每条不超过 256 字符。");
        warnings.Add("请自行确认启动参数、程序工作方式及业务完成依据；本检查不会运行程序。");
        warnings.Add("本检查无法证明日志写入来源，也无法确认外部进程或更新重启是否已结束。");
        if (task.SharedLog) warnings.Add("共享日志无法确认本次来源，运行后仍需人工核实。");
        if (!task.TrustCompletion && task.Completion != "人工确认") warnings.Add("自动进行下一项未开启，本次完成后仍可能暂停等待核实。");
        if (task.Completion == "人工确认") warnings.Add("人工确认仍需等待受管进程退出。");
        if (task.Completion == "MAA日志") warnings.Add("MAA 版本及实际日志语义须人工确认，本检查不证明受支持。");
        return new(errors, warnings);
    }
}
