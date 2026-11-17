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
        if (task.CloseAfterManualConfirmation && task.Completion != "人工确认") errors.Add("人工确认后请求关闭只适用于人工确认完成方式。");
        if (task.CloseAfterLogSignal && (task.Completion != "日志信号" || task.LogSource != "日志文件" || task.SharedLog || !task.CloseLogExclusiveConfirmed || !task.TrustCompletion)) errors.Add("日志完成后请求关闭需要确认日志不共享、日志信号完成方式及开启自动进行下一项。");
        if (task.StartWithSelectedButton && (string.IsNullOrWhiteSpace(task.StartButtonName) && string.IsNullOrWhiteSpace(task.StartButtonId)) && task.VisualMode == "无") errors.Add("请先选择程序启动按钮。");
        if (task.StartWithSelectedButton && task.VisualMode is not ("无" or "文字" or "图片")) errors.Add("视觉识别方式无效。");
        if (task.StartWithSelectedButton && task.VisualMode != "无" && (task.VisualWidth <= 0 || task.VisualHeight <= 0 || (long)task.VisualWidth * task.VisualHeight > 262144 || task.VisualX < 0 || task.VisualY < 0 || task.VisualWindowWidth <= 0 || task.VisualWindowHeight <= 0 || (long)task.VisualX + task.VisualWidth > task.VisualWindowWidth || (long)task.VisualY + task.VisualHeight > task.VisualWindowHeight)) errors.Add("请先选择有效的窗口目标区域。");
        if (task.StartWithSelectedButton && task.VisualMode == "文字" && string.IsNullOrWhiteSpace(task.VisualText)) errors.Add("请填写按钮文字。");
        if (task.StartWithSelectedButton && task.VisualMode == "图片" && !File.Exists(task.VisualTemplatePath)) errors.Add("图片模板不存在。");
        if (task.StartWithSelectedButton && task.VisualMode != "无" && task.Completion != "人工确认" && (task.Completion != "日志信号" || task.LogSource != "日志文件" || task.SharedLog || !task.CloseLogExclusiveConfirmed)) errors.Add("视觉启动需要人工确认，或使用明确确认不共享的日志文件作为完成依据。");
        if (task.StartWithSelectedButton && !task.StartManualLaunchConfirmed) errors.Add("请确认程序打开后不会自行开始任务。");
        if (task.StartWithSelectedButton && task.MaaStartByRunner) errors.Add("不能同时启用通用启动按钮和 MAA 专用启动。");
        if (task.StartWithSelectedButton && task.Completion == "MAA日志") errors.Add("MAA 日志任务请使用专用启动选项。");
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
