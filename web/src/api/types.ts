// Типы ответов API (contracts/openapi.json). Деньги — целые еврокопейки.

export type RiskLevel = 'high' | 'medium' | 'low' | 'unknown';
export type ScoreState = 'complete' | 'partial' | 'no_renewable_services' | 'insufficient_data';

/** Чем считается основной балл: rules — правила, ml — только ML-модель */
export type ScoringMode = 'rules' | 'ml';
/** Чем посчитано поколение; fallback — почему выбран ML, а посчитано правилами */
export interface ScoringInfo {
  requested: ScoringMode; used: ScoringMode; modelId: number | null; modelVersion: string | null; fallback: 'no_model' | 'ml_failed' | null;
}
export interface GenerationInfo {
  id: number; sourceRevision: number; modelRevision: number; asOf: string; sourceAsOf: string; sourceAsOfAssumed: boolean;
  publishedAt: string; clockMode: 'live' | 'source'; engineVersion: string; synthetic: boolean; sourceAgeSeconds: number; horizonDays: number;
  /** Водяной знак журнала событий; sourceUpdatedAt — последнее изменение источника (событие или загрузка тестовых данных). */
  sourceSeq: number; sourceUpdatedAt: string; sourceKind: 'snapshot' | 'stream';
  /** Последнее доменное событие, учтённое в активности клиентов */
  activitySeq: number;
  /** ML-модель поколения: в тени или основной балл (см. scoring) */
  mlModelId?: number | null;
  /** Чем посчитан основной балл этого поколения */
  scoring?: ScoringMode;
}
export interface PendingInfo { jobId: number; status: string; reason: string; modelRevision: number | null; sourceRevision: number | null; createdAt: string; }
export interface LastFailureInfo { jobId: number; errorCode: string | null; finishedAt: string | null; modelRevision: number | null; sourceRevision: number | null; }

export interface Reason {
  code: string; featureKey: string; title: string; text: string; serviceId: number | null; value: number; weight: number; contribution: number;
  evidence: Record<string, unknown>;
}
export interface Money { plannedRenewalsMinor: number; unfundedMinor: number; atRiskMinor: number; }

export type FullChurnState = 'scored' | 'partial' | 'churned' | 'blocked' | 'no_services' | 'insufficient_data';
export type PaymentTrendState = 'decline' | 'stable' | 'growth' | 'no_payments' | 'insufficient_history';
export interface ChurnRow {
  fullChurn: { score: number | null; level: RiskLevel; state: FullChurnState };
  paymentTrend: { state: PaymentTrendState; changePct: number | null; currentMinor: number; previousMinor: number };
}
export interface ChurnDetail {
  fullChurn: { score: number | null; level: RiskLevel; state: FullChurnState; weakestServiceId: number | null; text: string };
  paymentTrend: { state: PaymentTrendState; changePct: number | null; currentMinor: number; previousMinor: number; windowDays: number; historyStart: string | null; text: string };
}

export interface CustomerListItem {
  id: number; email: string; balance: number | null; totalReplenished: number | null; withdrawBalance: number | null; bonusBalance: number | null;
  bonusState: string | null; riskScore: number | null; riskLevel: RiskLevel; scoreState: ScoreState; primaryReason: Reason; reasonCodes: string[];
  flags: string[]; money: Money; services: { total: number; renewable: number; scored: number }; churn: ChurnRow;
}
export interface CustomerListResponse {
  generation: GenerationInfo; pending: PendingInfo | null; page: number; pageSize: number; total: number; sortBy: string; sortDirection: 'asc' | 'desc';
  items: CustomerListItem[];
}

export interface Feature { key: string; title: string; value: number | null; weight: number; contribution: number; missingInputs: string[]; evidence: Record<string, unknown>; text: string; }
export interface Transaction { id: number; serviceId: number; amount: number | null; status: string | null; type: string | null; createdAt: string | null; performedAt: string | null; }
export interface Service {
  id: number; name: string; status: string | null; paymentTerm: string | null; autoProlong: boolean | null; createdAt: string | null; expiresAt: string | null;
  price: number | null; renewable: boolean; scored: boolean; score: number | null; rawScore: number | null; capped: boolean; features: Feature[];
  nextDue: string | null; renewalsInHorizon: number; firstDue: string | null; money: Money; flags: string[]; missingInputs: string[]; lastTransaction: Transaction | null;
}
export interface Funding {
  known: boolean; fundsMinor: number; balanceAvailableMinor: number; bonusAvailableMinor: number; balanceUsedMinor: number; bonusUsedMinor: number;
  bonusEligible: boolean; bonusState: string | null; withdrawExcludedMinor: number; firstUnfundedDue: string | null;
}
export interface CustomerDetail {
  customer: { id: number; email: string; balance: number | null; withdrawBalance: number | null; totalReplenished: number | null; bonusBalance: number | null; bonusState: string | null };
  riskScore: number | null; riskLevel: RiskLevel; scoreState: ScoreState; primaryReason: Reason; reasons: Reason[]; services: Service[]; funding: Funding;
  money: Money; unscoredPlannedMinor: number; flags: string[]; serviceCounts: { total: number; renewable: number; scored: number };
  transactions: Transaction[]; moneyFormula: string; churn: ChurnDetail | null;
  /** Теневая модель: рядом с правилами, не меняет score */
  mlShadow?: MlShadowDetail | null;
}
export interface MlShadowDetail {
  modelId: number; modelVersion: string; state: 'scored' | 'out_of_scope' | 'failed'; probability: number | null; serviceId: number | null;
  services: { serviceId: number; probability: number; daysToExpiry: number; rulesScore: number | null; drivers?: MlDriver[] | null }[];
  /** treeshap — точные вклады деревьев, occlusion — сравнение с типичным клиентом */
  explain?: 'treeshap' | 'occlusion' | null;
}
/** Вклад фактора в прогноз: насколько вероятность выше, чем при этом факторе как у типичного клиента */
export interface MlDriver { key: string; title: string; delta: number; text: string; }
export interface CustomerDetailResponse { generation: GenerationInfo; pending: PendingInfo | null; detail: CustomerDetail; }

export interface LevelMoney { customers: number; plannedRenewalsMinor: number; unfundedMinor: number; atRiskMinor: number; }
export interface SummaryResponse {
  generation: GenerationInfo; pending: PendingInfo | null; lastFailure: LastFailureInfo | null;
  customers: { total: number; byLevel: Record<RiskLevel, number>; byState: Record<ScoreState, number>; scored: number; scoreSum: number };
  services: { total: number; renewable: number; scored: number; unscored: number; unschedulable: number; byStatus: Record<string, number> };
  money: {
    horizonDays: number; plannedRenewalsMinor: number; unfundedMinor: number; atRiskMinor: number; unscoredPlannedMinor: number; unfundedUnknownPlannedMinor: number;
    byLevel: Record<RiskLevel, LevelMoney>;
    /** 90 % интервал выручки под риском — только когда балл считает ML (балл — вероятность) */
    atRiskInterval?: { expectedMinor: number; lowMinor: number; highMinor: number; level: number; items: number } | null;
  };
  qualityFlags: Record<string, number>;
  topAtRisk: { id: number; email: string; riskScore: number | null; riskLevel: RiskLevel; atRiskMinor: number; primaryReasonText: string }[];
  forecast: { date: string; plannedMinor: number; atRiskMinor: number; renewals: number }[];
  history: { observedSnapshots: number; eventsTotal: number; deletedByObservedDay: Record<string, number>; recentEvents: ObservedEvent[]; note: string };
  definitions: { atRisk: string; planned: string; unfunded: string; score: string; fullChurn: string; paymentTrend: string; expiryOutcomes: string };
  churn: {
    fullChurn: { byLevel: Record<RiskLevel, number>; byState: Record<FullChurnState, number> };
    paymentTrend: { byState: Record<PaymentTrendState, number>; windowDays: number };
  } | null;
}
export interface ObservedEvent { serviceId: number; ownerId: number; kind: string; fromStatus: string | null; toStatus: string | null; observedFrom: string; observedTo: string; sourceRevision: number; }

export interface ModelSettings {
  schemaVersion: number; horizonDays: number; urgencyWindowDays: number; thresholds: { medium: number; high: number };
  clock: { mode: 'live' | 'source'; tickMinutes: number }; weights: Record<string, number>;
  churn: { nonRenewalDays: number; paymentWindowDays: number; paymentDeclinePct: number } | null;
}
export interface Revision { revision: number; settings: ModelSettings; createdAt: string; createdBy: string; comment: string | null; basedOn: number | null; }
export interface ModelSettingsResponse {
  latest: Revision; active: { revision: number; generationId: number; asOf: string } | null;
  catalog: { key: string; title: string; description: string; defaultWeight: number; behavioral: boolean }[];
  limits: {
    maxHorizonDays: number; maxUrgencyWindowDays: number; minWeight: number; maxWeight: number; minTickMinutes: number; maxTickMinutes: number;
    maxNonRenewalDays: number; minPaymentWindowDays: number; maxPaymentWindowDays: number;
  };
  pending: boolean;
}
export interface PreviewResponse {
  asOf: string; basedOnGeneration: number; byLevelBefore: Record<string, number>; byLevelAfter: Record<string, number>;
  atRiskBefore: number; atRiskAfter: number; changedLevelCount: number;
  changes: { customerId: number; email: string; scoreBefore: number | null; scoreAfter: number | null; levelBefore: RiskLevel; levelAfter: RiskLevel; atRiskBefore: number; atRiskAfter: number }[];
}

export interface SnapshotInfo {
  revision: number; sourceAsOf: string; sourceAsOfAssumed: boolean; receivedAt: string; contentHash: string; rawSha256: string; adapter: string; schemaVersion: number;
  sourceRevisionLabel: string | null; synthetic: boolean; provenance: string; counts: { accounts: number; services: number; transactions: number };
  warnings: Record<string, number>; rawBytes: number; actor: string; active: boolean;
}
export interface ValidationReport {
  valid: boolean; counts: { accounts: number; services: number; transactions: number }; errors: Record<string, string[]>; errorCounts: Record<string, number>;
  warnings: Record<string, number>; sourceAsOf: string | null; sourceAsOfAssumed: boolean;
}
export interface UserInfo { login: string; role: 'admin' | 'analyst'; sessionExpiresAt: string | null; }

// Поток событий источника (только входящие коннекторы)
export type SourceOutcome = 'applied' | 'no_change' | 'stale';
export interface JournalItem {
  seq: number; receivedAt: string; occurredAt: string; eventType: string | null; kind: 'account' | 'service' | 'transaction'; entityId: number;
  ownerId: number | null; outcome: SourceOutcome; staleReason: string | null; changes: string[]; effect: Record<string, unknown>; origin: 'push'; synthetic: boolean;
}
export interface JournalPage { items: JournalItem[]; nextBeforeSeq: number | null; }
export interface StreamCoverage { streamStartedAt: string | null; lastEventAt: string | null; sourceUpdatedAt: string | null; events: number; synthetic: boolean; }
/** http-push (INGEST_TOKEN), batch-import (импорт истории), snapshot (тестовые данные), panel (администратор) или имя из Auth:IngestConnectors */
export interface Connector {
  key: string; enabled: boolean; status: 'ok' | 'idle' | 'silent';
  lastReceivedAt: string | null; lastOccurredAt: string | null; watermark: string | null; lagSeconds: number | null; events: number;
  rejected: number; duplicates: number; lastError: string | null; lastErrorAt: string | null; lastImport: ImportView | null;
}
/** Пакетный импорт истории */
export interface ImportView {
  id: number; createdAt: string; actor: string; connector: string; synthetic: boolean;
  status: 'uploading' | 'pending' | 'running' | 'succeeded' | 'failed'; bytes: number; chunksTotal: number; chunksDone: number;
  eventsTotal: number; applied: number; recorded: number; noChange: number; stale: number; duplicates: number; rejected: number;
  errors: string[]; errorCode: string | null; startedAt: string | null; finishedAt: string | null; jobId: number | null;
}
/** Поведение клиента по событиям v2 */
export interface ActivityDay {
  date: string; logins: number; cpuAvgPct: number | null; usageSamples: number; trafficOutGb: number; incidentMinutes: number;
  ticketsOpened: number; ticketsUnsolved: number; paymentsCount: number; paymentsMinor: number; paymentsFailed: number; topUpsMinor: number; events: number;
}
export type EventDomain = 'account' | 'money' | 'service' | 'usage' | 'support' | 'retention' | 'other';
export interface DomainEventItem {
  seq: number; occurredAt: string; receivedAt: string; type: string; domain: EventDomain; serviceId: number | null; text: string;
  knownType: boolean; connector: string; synthetic: boolean;
}
export interface CustomerActivity {
  knownSince: string | null; from: string; to: string; days: ActivityDay[]; events: DomainEventItem[]; nextBeforeSeq: number | null;
}
export interface StreamQuality { domainEvents24h: number; unknownTypes24h: number; accountsWithGaps: number; oldestOrphanAt: string | null; }
export interface StreamStatus {
  base: { revision: number; sourceAsOf: string; synthetic: boolean } | null; seq: number; sourceUpdatedAt: string | null; streamStartedAt: string | null;
  lastEventAt: string | null; eventsTotal: number; eventsSinceBase: number; last24h: Record<string, number>; orphanServices: number; orphanTransactions: number;
  synthetic: boolean; silent: boolean; silentAfterMinutes: number; quality: StreamQuality; connectors: Connector[];
}
export interface HistoryDay {
  date: string; renewed: number; toProlongWait: number; deleted: number; blocked: number; customersLostAll: number;
  paymentsMinor: number; payments: number; topUpsMinor: number; topUps: number;
}
export interface ExpiryOutcomes {
  graceDays: number; renewed: number; notRenewed: number; excluded: number; pending: number;
  byTerm: Record<string, { renewed: number; notRenewed: number }>; notRenewedRatePct: number | null;
}
export interface HistoryResponse { coverage: StreamCoverage; days: HistoryDay[]; outcomes: ExpiryOutcomes; note: string; }
export interface CustomerEventsResponse { coverage: StreamCoverage; items: JournalItem[]; }
export interface MlMetric { prAuc: number; rocAuc: number; k: number; precisionAtK: number; recallAtK: number; brier: number | null; }
export interface MlModelView {
  id: number; version: string; kind: string; featureSchema: string; synthetic: boolean; uploadedAt: string; uploadedBy: string; onnxSha256: string; onnxBytes: number;
}
export interface MlShadowStatus {
  model: MlModelView;
  offline: {
    model: string; baseline: 'rules' | 'rulesWide'; synthetic: boolean; testFrom: string | null; testRows: number; testPositives: number; trainRows: number;
    modelMetrics: MlMetric | null; rules: MlMetric | null; rulesWide: MlMetric | null;
    prAucCi95: number[] | null; prAucMinusRulesCi95: number[] | null; precisionAtKMinusRulesCi95: number[] | null;
    topFeatures: { name: string; prAucDrop: number }[]; shuffledLabelsRocAuc: number | null;
  };
  generation: {
    modelId: number; version: string; state: 'scored' | 'failed'; servicesInScope: number; customersScored: number; topK: number; topKOverlap: number;
    meanProbability: number | null; durationMs: number; errorCode: string | null;
  } | null;
  generationId: number | null;
  online: { predictions: number; matured: number; notRenewed: number; pending: number; model: MlMetric | null; rules: MlMetric | null };
  note: string;
}
export interface MlStatus {
  status: 'not_trained' | 'shadow'; reason: string; target: string; nextStep: string;
  labels: { renewed: number; notRenewed: number; pending: number; requiredPerClass: number; ready: boolean; runtime: string };
  shadow?: MlShadowStatus | null;
  /** Выбранный режим основного балла и чем посчитано активное поколение */
  scoring?: { primary: ScoringMode; generation: ScoringInfo | null } | null;
}

export type SimulationPhase = 'idle' | 'bootstrap' | 'history' | 'live' | 'paused' | 'error';
export type WorldSource = 'organizer' | 'generated';
export interface WorldSettings {
  source: WorldSource; seed: number; extraCustomers: number; customers: number; historyDays: number;
}
export interface SimulationSettings {
  running: boolean; tickSeconds: number; scenarioStepSeconds: number; chaos: boolean; world: WorldSettings;
}
export interface SimulationAgentStatus {
  agentVersion: string; worldRevision: number; runId: string | null; phase: SimulationPhase; simClock: string | null;
  customers: number; services: number; liveServices: number; eventsGenerated: number; eventsSent: number; backlog: number;
  lastSentAt: string | null; lastError: string | null; lastNarration: string | null; scenarioHandled: number;
}
export interface SimulationView {
  eventsOnly: boolean; settings: SimulationSettings; revision: number; updatedAt: string; updatedBy: string | null;
  world: { revision: number; startedAt: string; settings: WorldSettings; resetAt: string | null; resetBy: string | null };
  scenarioRequest: number;
  agent: { online: boolean; reportedAt: string | null; status: SimulationAgentStatus | null };
  pending: { worldChanged: boolean };
}

/** Конструктор событий: тип, поля формы, варианты */
export interface ComposerOption { value: string; label: string }
export interface ComposerField {
  name: string; label: string; kind: 'money' | 'number' | 'enum' | 'bool';
  options?: ComposerOption[] | null; min?: number | null; max?: number | null; default?: unknown;
}
export interface ComposerEventType { type: string; title: string; description: string; needsService: boolean; fields: ComposerField[] }
export type ComposedStatus = 'pending' | 'applied' | 'rejected';
export interface ComposedEvent {
  id: number; createdAt: string; createdBy: string; type: string; accountId: number; serviceId?: number | null;
  data: Record<string, unknown>; status: ComposedStatus; eventIds: string[]; message?: string | null; updatedAt?: string | null;
}
