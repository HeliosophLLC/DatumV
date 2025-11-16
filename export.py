from ultralytics import YOLO
model = YOLO("yolov8n.pt")    # auto-downloads yolov8n.pt on first run
model.export(format="onnx", imgsz=640, dynamic=True)