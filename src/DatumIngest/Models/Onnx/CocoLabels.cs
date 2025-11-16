namespace DatumIngest.Models.Onnx;

/// <summary>
/// The 80 COCO object-detection class names, in the canonical order Ultralytics
/// uses for YOLOv8 outputs. Index <c>i</c> here corresponds to class score
/// channel <c>i</c> in the ONNX graph's <c>[N, 84, 8400]</c> output (channels
/// 4..83 are the 80 class scores).
/// </summary>
internal static class CocoLabels
{
    public static readonly IReadOnlyList<string> Names =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck",
        "boat", "traffic light", "fire hydrant", "stop sign", "parking meter", "bench",
        "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
        "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard",
        "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
        "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl",
        "banana", "apple", "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza",
        "donut", "cake", "chair", "couch", "potted plant", "bed", "dining table", "toilet",
        "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave", "oven",
        "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors",
        "teddy bear", "hair drier", "toothbrush",
    ];
}
