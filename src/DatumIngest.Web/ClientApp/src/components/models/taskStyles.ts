import {
  Activity,
  AlignLeft,
  AudioLines,
  AudioWaveform,
  Boxes,
  Brush,
  Captions,
  Combine,
  Crosshair,
  Eraser,
  Film,
  Frame,
  ImagePlay,
  ImagePlus,
  Languages,
  Layers,
  LineChart,
  MapPin,
  MessageCircleQuestion,
  MessageSquareText,
  MessagesSquare,
  Mic,
  MicVocal,
  Mountain,
  Palette,
  Pencil,
  Ruler,
  ScanText,
  Smile,
  Sparkles,
  Speaker,
  Square,
  Table,
  Tag,
  Tags,
  ToggleLeft,
  TrendingUp,
  Triangle,
  Type,
  WandSparkles,
  ZoomIn,
  type LucideIcon,
} from 'lucide-react';
import type { CatalogTaskInfoSnapshot } from '@/state/models';

// Left-border accent color per task family. Tailwind JIT only emits
// classes that appear verbatim in source, so each branch must spell out
// the full literal — no `border-l-${color}` interpolation. Unknown
// families render with a transparent border so the layout stays stable.
export function familyAccentClass(family: string): string {
  switch (family) {
    case 'Multimodal': return 'border-l-orange-500';
    case 'ComputerVision': return 'border-l-blue-500';
    case 'NaturalLanguageProcessing': return 'border-l-red-500';
    case 'Audio': return 'border-l-emerald-500';
    case 'Tabular': return 'border-l-violet-500';
    default: return 'border-l-transparent';
  }
}

// Selected-chip background color, scaled to the family accent. Same
// JIT-literal rule as `familyAccentClass`.
export function familySelectedBackgroundClass(family: string): string {
  switch (family) {
    case 'Multimodal': return 'bg-orange-500/20';
    case 'ComputerVision': return 'bg-blue-500/20';
    case 'NaturalLanguageProcessing': return 'bg-red-500/20';
    case 'Audio': return 'bg-emerald-500/20';
    case 'Tabular': return 'bg-violet-500/20';
    default: return 'bg-transparent';
  }
}

// Hover background for an unselected chip — same family hue as the
// selected background but at half intensity (10% vs 20% opacity). Same
// JIT-literal rule as `familyAccentClass`.
export function familyHoverBackgroundClass(family: string): string {
  switch (family) {
    case 'Multimodal': return 'hover:bg-orange-500/10';
    case 'ComputerVision': return 'hover:bg-blue-500/10';
    case 'NaturalLanguageProcessing': return 'hover:bg-red-500/10';
    case 'Audio': return 'hover:bg-emerald-500/10';
    case 'Tabular': return 'hover:bg-violet-500/10';
    default: return '';
  }
}

// Lucide icon per task contract. Used by the model-row chips when the
// list pane is too narrow to fit human-friendly labels — the icon
// carries the contract identity and the localized label moves to the
// `title` tooltip. Unknown / newly-added contracts fall back to a
// generic Tag so they still render with the family accent.
const TASK_ICONS: Readonly<Record<string, LucideIcon>> = {
  // Natural Language Processing
  TextEmbedder: Sparkles,
  TextClassifier: Tag,
  LabeledTextClassifier: Tag,
  TextMultiClassifier: Tags,
  LabeledTextMultiClassifier: Tags,
  BinaryTextClassifier: ToggleLeft,
  TokenClassifier: Type,
  TextPairScorer: Combine,
  TextReranker: AlignLeft,
  TextGenerator: WandSparkles,
  ChatCompleter: MessagesSquare,
  Translator: Languages,
  TextSummarizer: AlignLeft,
  TextEditor: Pencil,
  // Computer Vision
  ImageClassifier: Tag,
  LabeledImageClassifier: Tag,
  ImageMultiClassifier: Tags,
  BinaryImageClassifier: ToggleLeft,
  ImageTagger: Tags,
  ImageEmbedder: Sparkles,
  ImageCaptioner: MessageSquareText,
  ObjectDetector: Crosshair,
  LabeledObjectDetector: Crosshair,
  RegionLocalizer: Frame,
  TextDetector: ScanText,
  TextRecognizer: ScanText,
  TextOCR: ScanText,
  FaceDetector: Smile,
  KeypointDetector: MapPin,
  SemanticSegmenter: Layers,
  InstanceSegmenter: Layers,
  PointSegmenter: Layers,
  BoxSegmenter: Square,
  BackgroundRemover: Eraser,
  DepthEstimator: Mountain,
  DepthEstimatorMetric: Ruler,
  SurfaceNormalEstimator: Triangle,
  StereoDepthEstimator: Mountain,
  MeshFromImage: Boxes,
  ImageUpscaler: ZoomIn,
  ImageRestorer: WandSparkles,
  ImageColorizer: Palette,
  ImageStyleTransfer: Brush,
  ImageEditor: Pencil,
  VideoClassifier: Film,
  VideoSegmentClassifier: Film,
  VideoEmbedder: Film,
  // Audio
  AudioClassifier: AudioLines,
  AudioMultiClassifier: AudioLines,
  AudioEmbedder: AudioLines,
  AudioToText: Captions,
  AudioToTextTimed: Captions,
  TextToAudio: Speaker,
  VoiceCloner: MicVocal,
  AudioRestorer: AudioWaveform,
  VoiceActivityDetector: Mic,
  // Multimodal
  VisualQA: MessageCircleQuestion,
  ImageTextSimilarity: Combine,
  ImageTextEmbedder: Combine,
  ZeroShotImageClassifier: Sparkles,
  ZeroShotObjectDetector: Crosshair,
  TextToImage: ImagePlus,
  ImageToImage: ImagePlay,
  // Tabular
  TabularClassifier: Table,
  TabularRegressor: TrendingUp,
  TimeSeriesClassifier: LineChart,
  TimeSeriesForecaster: TrendingUp,
  TimeSeriesAnomalyDetector: Activity,
};

export function taskIcon(name: string): LucideIcon {
  return TASK_ICONS[name] ?? Tag;
}

// Build a name → family lookup over the task vocabulary. Names are
// matched case-insensitively to mirror the rest of the catalog
// (manifest entries occasionally case-mix).
export function buildTaskFamilyMap(
  tasks: readonly CatalogTaskInfoSnapshot[] | null,
): ReadonlyMap<string, string> {
  const map = new Map<string, string>();
  if (tasks === null) return map;
  for (const task of tasks) {
    if (task.name) map.set(task.name.toLowerCase(), task.family ?? '');
  }
  return map;
}
