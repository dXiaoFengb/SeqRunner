using System.Text;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length != 1 || (args[0] != "--stdin" && !File.Exists(args[0])))
{
    Console.Error.WriteLine("需要一个已有图片文件。");
    return 2;
}

try
{
    string models = Path.Combine(AppContext.BaseDirectory, "models");
    using var ocr = new RapidOcr();
    ocr.InitModels(Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
        Path.Combine(models, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
        Path.Combine(models, "ch_PP-OCRv5_rec_mobile_infer.onnx"),
        Path.Combine(models, "ppocrv5_dict.txt"));
    using var bitmap = args[0] == "--stdin" ? SKBitmap.Decode(Console.OpenStandardInput()) : SKBitmap.Decode(args[0]);
    if (bitmap == null) throw new InvalidDataException("图片格式无效");
    var result = ocr.Detect(bitmap, RapidOcrOptions.Default);
    var lines = result.TextBlocks.Select(block => new
    {
        text = block.Text,
        confidence = block.CharScores is { Length: > 0 } ? block.CharScores.Average() : (float?)null,
        points = block.BoxPoints.Select(point => new { x = point.X, y = point.Y }).ToArray()
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { lines }));
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TypeInitializationException or DllNotFoundException)
{
    Console.Error.WriteLine("OCR 识别失败：" + e.GetType().Name);
    return 1;
}
