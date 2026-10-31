/* Generated from server/openapi/u-vision.json by scripts/generate-types.mjs — do not edit. */

/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AuthorityStage".
 */
export type AuthorityStage = 'shadow' | 'advisory' | 'co_primary' | 'ml_primary'
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "IFormFile".
 */
export type IFormFile = string
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "Verdict".
 */
export type Verdict = 'OK' | 'NG'
/**
 * 0 = ok reference, 1 = ng reference
 *
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ReferenceLabel".
 */
export type ReferenceLabel = 0 | 1
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "Stream".
 */
export type Stream = string

export interface Contract {
  AdminSetupInput?: AdminSetupInput
  AdminSetupRequiredResponse?: AdminSetupRequiredResponse
  AdminStatusResponse?: AdminStatusResponse
  AmbiguityGroup?: AmbiguityGroup
  AmbiguityResponse?: AmbiguityResponse
  AnomalyCrop?: AnomalyCrop
  AnomalyMap?: AnomalyMap
  AuditInput?: AuditInput
  AuditResponse?: AuditResponse
  AuthorityStage?: AuthorityStage
  AuthorityState?: AuthorityState
  AuthorityTransition?: AuthorityTransition
  AuthorityTransitionResponse?: AuthorityTransitionResponse
  ContestedBy?: ContestedBy
  CountBox?: CountBox
  CountInfo?: CountInfo
  CountingMetrics?: CountingMetrics
  CountItemInfo?: CountItemInfo
  DatasetClassCount?: DatasetClassCount
  DatasetExportManifest?: DatasetExportManifest
  DatasetItem?: DatasetItem
  DatasetSelection?: DatasetSelection
  DataVolumeStatus?: DataVolumeStatus
  DemoteAuthorityInput?: DemoteAuthorityInput
  DetectionUnavailableResponse?: DetectionUnavailableResponse
  EventsStatusResponse?: EventsStatusResponse
  HealthResponse?: HealthResponse
  IFormFile?: IFormFile
  InspectResponse?: InspectResponse
  InspectResultV2?: InspectResultV2
  LabelAudit?: LabelAudit
  LabelEvent?: LabelEvent
  LabelInput?: LabelInput
  MemoryReplayRow?: MemoryReplayRow
  MemoryReport?: MemoryReport
  MetricsSummary?: MetricsSummary
  MlResult?: MlResult
  ModelListResponse?: ModelListResponse
  ModelPointerResponse?: ModelPointerResponse
  ModelRegisteredResponse?: ModelRegisteredResponse
  ModelVersionManifest?: ModelVersionManifest
  PathStepV2?: PathStepV2
  ProblemDetails?: ProblemDetails
  PromoteAuthorityInput?: PromoteAuthorityInput
  QuestionAnswerInput?: QuestionAnswerInput
  QuestionAnswerResponse?: QuestionAnswerResponse
  QuestionResponse?: QuestionResponse
  ReferenceInfo?: ReferenceInfo
  ReferenceLabel?: ReferenceLabel
  RegisterModelInput?: RegisterModelInput
  RetentionKindSummary?: RetentionKindSummary
  RetentionLoss?: RetentionLoss
  RetentionPurgeInput?: RetentionPurgeInput
  RetentionPurgeResponse?: RetentionPurgeResponse
  Roi?: Roi
  Scenario?: Scenario
  ScenarioClass?: ScenarioClass
  ScenarioInput?: ScenarioInput
  ScenarioJudge?: ScenarioJudge
  ScenarioTargets?: ScenarioTargets
  ScenarioTraining?: ScenarioTraining
  ScenarioTrust?: ScenarioTrust
  SettledBy?: SettledBy
  SolverTrustDevice?: SolverTrustDevice
  SolverTrustResponse?: SolverTrustResponse
  StoredLabel?: StoredLabel
  StoredResult?: StoredResult
  Stream?: Stream
  TrainingClassCountDto?: TrainingClassCountDto
  TrainingReadinessResponse?: TrainingReadinessResponse
  Verdict?: Verdict
  WebhookStatus?: WebhookStatus
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AdminSetupInput".
 */
export interface AdminSetupInput {
  setup_token?: null | string
  new_pin?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AdminSetupRequiredResponse".
 */
export interface AdminSetupRequiredResponse {
  setup_required: boolean
  detail: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AdminStatusResponse".
 */
export interface AdminStatusResponse {
  pin_configured: boolean
  setup_required: boolean
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AmbiguityGroup".
 */
export interface AmbiguityGroup {
  answered: number
  abstained: number
  open: number
  abstain_rate?: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AmbiguityResponse".
 */
export interface AmbiguityResponse {
  scenario_id: string
  min_answers_for_rate: number
  representative: AmbiguityGroup
  targeted: AmbiguityGroup
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AnomalyCrop".
 */
export interface AnomalyCrop {
  x0: number
  y0: number
  x1: number
  y1: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AnomalyMap".
 */
export interface AnomalyMap {
  encoder: string
  rows: number
  columns: number
  crop?: AnomalyCrop
  distances: number[]
  patch_score: number
  global_score: number
  bank_size: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AuditInput".
 */
export interface AuditInput {
  scenario_id: string
  date: string
  image_id: string
  label: string
  by?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AuditResponse".
 */
export interface AuditResponse {
  status: string
  prior_label: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AuthorityState".
 */
export interface AuthorityState {
  stage: AuthorityStage
  previous_stage: null | AuthorityStage
  updated_at: string
  updated_by: string
  reason: null | string
  history: AuthorityTransition[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AuthorityTransition".
 */
export interface AuthorityTransition {
  from: string
  to: string
  at: string
  by: string
  mode: string
  reason: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "AuthorityTransitionResponse".
 */
export interface AuthorityTransitionResponse {
  stage: AuthorityStage
  previous_stage: AuthorityStage
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ContestedBy".
 */
export interface ContestedBy {
  tier: string
  class: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "CountBox".
 */
export interface CountBox {
  class: string
  x: number
  y: number
  w: number
  h: number
  confidence: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "CountInfo".
 */
export interface CountInfo {
  total: number
  by_class: {
    [k: string]: number
  }
  expected: number
  tolerance: number
  items?: CountItemInfo[]
  extra_policy?: string
  min_confidence: number
  uncertain: boolean
  boxes: CountBox[]
  near_boxes?: CountBox[]
  model_sha256?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "CountItemInfo".
 */
export interface CountItemInfo {
  class: string
  expected: number
  found: number
  status: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "CountingMetrics".
 */
export interface CountingMetrics {
  checked: number
  unavailable: number
  ng: number
  uncertain: number
  dataset_checked: number
  composition_checked: number
  with_missing: number
  with_extra: number
  missing_by_kind: {
    [k: string]: number
  }
  extra_by_kind: {
    [k: string]: number
  }
  ng_rate: null | number
  uncertain_rate: null | number
  unavailable_rate: null | number
  missing_rate: null | number
  extra_rate: null | number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DatasetClassCount".
 */
export interface DatasetClassCount {
  class_dir: string
  count: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DatasetExportManifest".
 */
export interface DatasetExportManifest {
  export_id: string
  scenario_id: string
  created_at: string
  images_root: string
  total: number
  classes: DatasetClassCount[]
  warnings: string[]
  items: DatasetItem[]
  selection?: DatasetSelection
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DatasetItem".
 */
export interface DatasetItem {
  image_id: string
  date: string
  label: string
  class_dir: string
  image_file: string
  stream?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DatasetSelection".
 */
export interface DatasetSelection {
  ok_ratio_cap: number
  hard_negatives: number
  dropped_conflicted: number
  dropped_ok_over_cap: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DataVolumeStatus".
 */
export interface DataVolumeStatus {
  free_bytes: number
  total_bytes: number
  low: boolean
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DemoteAuthorityInput".
 */
export interface DemoteAuthorityInput {
  by?: null | string
  reason?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "DetectionUnavailableResponse".
 */
export interface DetectionUnavailableResponse {
  detection_unavailable: boolean
  reason: string
  ml_hint?: MlResult
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "MlResult".
 */
export interface MlResult {
  label: string
  confidence: number
  model_version?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "EventsStatusResponse".
 */
export interface EventsStatusResponse {
  enabled: boolean
  webhooks: WebhookStatus[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "WebhookStatus".
 */
export interface WebhookStatus {
  name: string
  pending: number
  dead: number
  oldest_pending_at?: string
  last_error?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "HealthResponse".
 */
export interface HealthResponse {
  status: string
  provider: string
  data_volume?: DataVolumeStatus
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "InspectResponse".
 */
export interface InspectResponse {
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  image_id: string
  ml?: MlResult
  agreement?: boolean
  requires_review?: boolean
  posture?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "InspectResultV2".
 */
export interface InspectResultV2 {
  decision_id: string
  scenario_id: string
  timestamp: string
  class?: string
  settled_by?: SettledBy
  confidence?: number
  findings?: string
  requires_review?: boolean
  posture?: string
  contested_by?: ContestedBy[]
  path?: PathStepV2[]
  count?: CountInfo
  detection_unavailable?: boolean
  reason?: string
  device_id?: string
  device_label?: string
  captured_at?: string
  origin?: string
  origin_ref?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "SettledBy".
 */
export interface SettledBy {
  tier: string
  kind: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "PathStepV2".
 */
export interface PathStepV2 {
  tier: string
  outcome: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "LabelAudit".
 */
export interface LabelAudit {
  status: string
  at?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "LabelEvent".
 */
export interface LabelEvent {
  label: string
  by: string
  at: string
  mode: string
  stream?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "LabelInput".
 */
export interface LabelInput {
  scenario_id: string
  date: string
  image_id: string
  label: string
  by?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "MemoryReplayRow".
 */
export interface MemoryReplayRow {
  max_distance: number
  configured: boolean
  answered: number
  teacher_checked: number
  teacher_agreement: null | number
  labeled: number
  label_agreement: null | number
  missed: number
  miss_rate: null | number
  labeled_defects: number
  defect_pass_rate: null | number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "MemoryReport".
 */
export interface MemoryReport {
  scenario_id: string
  date: string
  enabled: boolean
  encoder?: string
  cases?: number
  built_at?: string
  replayed: number
  replay: MemoryReplayRow[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "MetricsSummary".
 */
export interface MetricsSummary {
  scenario_id: string
  date: string
  inspections: number
  ml_checked: number
  match_mismatches: number
  match_abstained: number
  contested: number
  dataset_inspections: number
  ml_degraded: number
  fail_closed: number
  agreements: number
  reviews_required: number
  audited: number
  label_consistent: number
  label_conflicts_open: number
  audit_ng_ng: number
  audit_ng_ok: number
  audit_ok_ng: number
  audit_ok_ok: number
  oracle_evaluated: number
  oracle_conflicts: number
  oracle_pending: number
  labeled: number
  labeled_ng: number
  vlm_ng_hits: number
  labeled_ok: number
  vlm_false_alarms: number
  ml_ok_scored: number
  ml_false_alarms: number
  ml_ng_scored: number
  ml_ng_hits: number
  agreement_rate: null | number
  review_rate: null | number
  degrade_rate: null | number
  vlm_ng_recall: null | number
  ml_ng_recall: null | number
  vlm_false_alarm_rate: null | number
  ml_false_alarm_rate: null | number
  fail_closed_rate: null | number
  label_consistency_rate: null | number
  audit_positive_agreement: null | number
  audit_negative_agreement: null | number
  oracle_consistency_rate: null | number
  oracle_coverage_rate: null | number
  promotion_eligible?: boolean
  counting?: CountingMetrics
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ModelListResponse".
 */
export interface ModelListResponse {
  active_version: null | string
  previous_version: null | string
  versions: ModelVersionManifest[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ModelVersionManifest".
 */
export interface ModelVersionManifest {
  version: string
  scenario_id: string
  model_name: string
  endpoint: null | string
  export_id: null | string
  metrics: null | {
    [k: string]: number
  }
  created_at: string
  created_by: string
  note: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ModelPointerResponse".
 */
export interface ModelPointerResponse {
  active_version: null | string
  previous_version: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ModelRegisteredResponse".
 */
export interface ModelRegisteredResponse {
  version: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ProblemDetails".
 */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "PromoteAuthorityInput".
 */
export interface PromoteAuthorityInput {
  stage: AuthorityStage
  by?: null | string
  reason?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "QuestionAnswerInput".
 */
export interface QuestionAnswerInput {
  scenario_id: string
  question_id: string
  choice?: string
  abstain?: boolean
  by?: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "QuestionAnswerResponse".
 */
export interface QuestionAnswerResponse {
  question_id: string
  recorded: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "QuestionResponse".
 */
export interface QuestionResponse {
  question_id: string
  scenario_id: string
  date: string
  image_id: string
  form: string
  choices: string[]
  reason: string
  created_at: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ReferenceInfo".
 */
export interface ReferenceInfo {
  ref_id: string
  label: ReferenceLabel
  ng_label: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "RegisterModelInput".
 */
export interface RegisterModelInput {
  model_name: string
  export_id?: null | string
  endpoint?: null | string
  metrics?: null | {
    [k: string]: number
  }
  note?: null | string
  by?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "RetentionKindSummary".
 */
export interface RetentionKindSummary {
  kind: string
  days: number
  files: number
  bytes: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "RetentionLoss".
 */
export interface RetentionLoss {
  results: number
  human_labels: number
  labels_not_exported: number
  open_questions: number
  decisions: number
  metric_rows: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "RetentionPurgeInput".
 */
export interface RetentionPurgeInput {
  from: string
  to: string
  kinds: string[]
  dry_run?: null | boolean
  plan_fingerprint?: null | string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "RetentionPurgeResponse".
 */
export interface RetentionPurgeResponse {
  scenario_id: string
  from: string
  to: string
  dry_run: boolean
  purged: boolean
  plan_fingerprint: string
  kinds: RetentionKindSummary[]
  lost: RetentionLoss
  covered_by_export?: boolean
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "Roi".
 */
export interface Roi {
  x?: number
  y?: number
  w?: number
  h?: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "Scenario".
 */
export interface Scenario {
  scenario_id: string
  name: string
  criteria: string
  roi: Roi
  motion_threshold: number
  still_frames: number
  min_sharpness: number
  max_image_dimension: number
  reference_cap: number
  ng_labels: {
    [k: string]: string
  }
  allow_cloud_egress: boolean
  type?: string
  trigger?: string
  classes?: ScenarioClass[]
  judges?: ScenarioJudge[]
  trust?: ScenarioTrust
  patch_encoder?: string
  expected_count?: number
  count_tolerance?: number
  expected_by_class?: {
    [k: string]: number
  }
  extra_policy?: string
  min_confidence?: number
  count_classes?: string[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ScenarioClass".
 */
export interface ScenarioClass {
  id: string
  label?: string
  settleable?: boolean
  must_catch?: boolean
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ScenarioJudge".
 */
export interface ScenarioJudge {
  tier: string
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ScenarioTrust".
 */
export interface ScenarioTrust {
  slice_key?: string
  targets?: ScenarioTargets
  training?: ScenarioTraining
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ScenarioTargets".
 */
export interface ScenarioTargets {
  agreement?: number
  max_miss_rate?: number
  sample_size?: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ScenarioTraining".
 */
export interface ScenarioTraining {
  min_per_class?: number
  ok_ratio_cap?: number
  rebuild_min_new_labels?: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "ScenarioInput".
 */
export interface ScenarioInput {
  scenario_id?: string
  name: string
  criteria?: string
  roi?: Roi
  motion_threshold?: number
  still_frames?: number
  min_sharpness?: number
  max_image_dimension?: number
  reference_cap?: number
  ng_labels?: {
    [k: string]: string
  }
  allow_cloud_egress?: boolean
  type?: null | string
  trigger?: null | string
  classes?: null | ScenarioClass[]
  judges?: null | ScenarioJudge[]
  trust?: null | ScenarioTrust
  patch_encoder?: null | string
  expected_count?: null | number
  count_tolerance?: null | number
  expected_by_class?: null | {
    [k: string]: number
  }
  extra_policy?: null | string
  min_confidence?: null | number
  count_classes?: null | string[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "SolverTrustDevice".
 */
export interface SolverTrustDevice {
  device: string
  planted: number
  correct: number
  wrong: number
  unsure: number
  open: number
  accuracy?: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "SolverTrustResponse".
 */
export interface SolverTrustResponse {
  scenario_id: string
  planted: number
  correct: number
  wrong: number
  unsure: number
  open: number
  min_answers_for_rate: number
  accuracy?: number
  devices: SolverTrustDevice[]
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "StoredLabel".
 */
export interface StoredLabel {
  image_id: string
  label?: string
  timestamp: string
  history?: LabelEvent[]
  audit?: LabelAudit
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "StoredResult".
 */
export interface StoredResult {
  scenario_id: string
  image_id: string
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  image_file: string
  device_id: string
  device_label: string
  ml?: MlResult
  agreement?: boolean
  requires_review?: boolean
  posture?: string
  expected_part?: string
  observed_code?: string
  captured_at?: string
  origin?: string
  origin_ref?: string
  class?: string
  count?: CountInfo
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "TrainingClassCountDto".
 */
export interface TrainingClassCountDto {
  id: string
  count: number
}
/**
 * This interface was referenced by `Contract`'s JSON-Schema
 * via the `definition` "TrainingReadinessResponse".
 */
export interface TrainingReadinessResponse {
  scenario_id: string
  classes: TrainingClassCountDto[]
  min_per_class: number
  build_ready?: boolean
  active_version?: string
  new_labels_since_model?: number
  rebuild_min_new_labels: number
  rebuild_suggested?: boolean
}
