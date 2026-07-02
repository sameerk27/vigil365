export type NavPage = "overview" | "recommendations" | "trends" | "identity" | "devices" | "email" | "incidents" | "alertcenter" | "compliance" | "servicehealth" | "network" | "licenses" | "conditionalaccess" | "auditlog" | "signinmap" | "users" | "setup";
export type AlertSeverity = "Informational" | "Low" | "Medium" | "High" | "Critical";
export type ServiceArea = "EntraId" | "Intune" | "DefenderXdr" | "ExchangeOnline" | "ServiceHealth";
export type Tone = "good" | "warning" | "error" | "neutral" | "info";

export type SecurityAlert = {
  id: number; externalId?: string; alertType: string; service: ServiceArea;
  severity: AlertSeverity; title: string; description?: string;
  userPrincipalName?: string; deviceName?: string; portalUrl?: string;
  detectedAt: string; lastUpdatedAt: string; isResolved: boolean;
};

export type Overview = {
  totalActive: number; highPriority: number;
  lastRun?: { startedAt: string; completedAt?: string; status: string; alertsUpserted: number; sourceFailures: number };
  byService: { service: string; count: number }[];
  trends: { date: string; severity: string; count: number }[];
  generatedAt: string;
};

export type SecureScore = {
  configured: boolean; currentScore: number; maxScore: number; percentage: number;
  trend: { date: string; score: number; maxScore: number }[]; error?: string;
};

export type IdentityData = {
  configured: boolean;
  mfa: { registered: number; total: number; percentage: number };
  guests: { total: number; active: number };
  riskyUsers: number;
  signIns: { total: number; failed: number; risky: number; foreign: number };
  foreignSignIns: { title: string; userPrincipalName?: string; detectedAt: string }[];
  recentAdminActivity: { activityDateTime?: string; activityDisplayName?: string; initiatedByUser?: string; result?: string }[];
};

export type DevicesData = {
  nonCompliant: number; notCheckedIn: number; totalDevices: number; compliancePct: number;
  nonCompliantDevices: { deviceName?: string; userPrincipalName?: string; description?: string; lastUpdatedAt: string }[];
};

export type ServiceHealthData = {
  total: number;
  issues: { title: string; description?: string; severity: string; detectedAt: string; portalUrl?: string }[];
};

export type LicenseSku = { name: string; consumed: number; purchased: number; available: number };
export type LicenseData = { configured: boolean; error?: string; skus: LicenseSku[]; totalPurchased: number; totalConsumed: number };

export type InactiveUser = { upn: string; name?: string; lastSignIn?: string; hasLicense: boolean; daysSince: number };
export type InactiveUsersData = { configured: boolean; error?: string; inactive90Count: number; neverSignedInCount: number; totalUsers: number; inactive90: InactiveUser[]; neverSignedIn: InactiveUser[] };

export type ExpiryUser = { upn: string; name?: string; daysUntilExpiry: number; lastChanged?: string };
export type PasswordExpiryData = { configured: boolean; error?: string; expiringSoonCount: number; expiredCount: number; neverExpiresCount: number; totalUsers: number; expiringSoon: ExpiryUser[]; expired: ExpiryUser[]; neverExpire: { upn: string; name?: string }[] };

export type CAPolicy = { name: string; state: string; inclUsers: string; exclUsers: string; apps: string; controls: string[] };
export type ConditionalAccessData = { configured: boolean; error?: string; enabled: number; disabled: number; reportOnly: number; policies: CAPolicy[] };

export type AuditEvent = { activityDateTime?: string; activityDisplayName?: string; category?: string; result?: string; resultReason?: string; initiatedByUser?: string; targetResources: string[] };
export type AuditLogData = { configured: boolean; error?: string; total: number; failures: number; events: AuditEvent[] };

export type SignInEntry = { upn?: string; app?: string; created?: string; city?: string; country?: string; success: boolean };
export type SignInLocationsData = { configured: boolean; error?: string; total: number; countries: number; failures: number; byCountry: { country: string; count: number; failures: number }[]; recent: SignInEntry[] };

export type DefenderAlert = {
  id?: string; title?: string; description?: string; severity: string; status: string;
  classification?: string; serviceSource?: string; detectionSource?: string; category?: string;
  createdDateTime?: string; lastUpdateDateTime?: string; assignedTo?: string;
  alertWebUrl?: string; incidentId?: string; mitreTechniques: string[] | string;
  recommendedActions?: string; actorDisplayName?: string; threatDisplayName?: string;
};
export type DefenderAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; bySource: Record<string,number>; alerts: DefenderAlert[] };

export type SecurityIncident = {
  id?: string; displayName?: string; severity: string; status: string;
  classification?: string; createdDateTime?: string; lastUpdateDateTime?: string;
  assignedTo?: string; incidentWebUrl?: string; customTags: string[];
  description?: string; recommendedActions?: string;
};
export type SecurityIncidentsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; incidents: SecurityIncident[] };

export type PrivilegedRole = { roleId?: string; roleName?: string; memberCount: number; members: { displayName?: string; userPrincipalName?: string }[] };
export type PrivilegedRolesData = { configured: boolean; error?: string; roles: PrivilegedRole[]; totalPrivilegedUsers: number };
export type DlpAlert = { id?: string; title?: string; severity: string; status: string; category?: string; serviceSource?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
export type DlpAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; bySource: Record<string,number>; alerts: DlpAlert[] };
export type MdeAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string; mitreTechniques: string[] };
export type MdeVulnerabilitiesData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; byCategory: Record<string,number>; alerts: MdeAlert[] };
export type PimActivation = { id?: string; action?: string; status?: string; createdDateTime?: string; justification?: string; principalDisplayName?: string; principalUpn?: string; roleName?: string };
export type PimData = { configured: boolean; error?: string; total: number; activations: PimActivation[] };
export type EmailProtectionAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
export type EmailProtectionData = { configured: boolean; error?: string; total: number; byCategory: Record<string,number>; bySeverity: Record<string,number>; alerts: EmailProtectionAlert[] };
export type PurviewLabel = { id?: string; name?: string; description?: string; color?: string; sensitivity: number; isActive: boolean };
export type PurviewData = { configured: boolean; error?: string; labelCount: number; labels: PurviewLabel[] };

export type MdiAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string; mitreTechniques: string[] };
export type MdiAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; byCategory: Record<string,number>; alerts: MdiAlert[] };

export type McasAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
export type McasAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; byCategory: Record<string,number>; alerts: McasAlert[] };

export type InsiderRiskAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
export type InsiderRiskData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; alerts: InsiderRiskAlert[] };

export type RiskDetection = { id?: string; riskEventType?: string; riskLevel: string; riskState: string; userDisplayName?: string; userPrincipalName?: string; lastUpdatedDateTime?: string; activityDateTime?: string; ipAddress?: string; city?: string; country?: string };
export type RiskDetectionsData = { configured: boolean; error?: string; total: number; byType: Record<string,number>; byLevel: Record<string,number>; detections: RiskDetection[] };

export type IdentityHealthIssue = { id?: string; displayName?: string; issueType?: string; severity: string; status: string; description?: string; recommendations?: string; createdDateTime?: string; domainNames: string[]; sensorDNSNames: string[] };
export type IdentityHealthData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; issues: IdentityHealthIssue[] };

export type AttackSim = { id?: string; displayName?: string; attackType?: string; status: string; createdDateTime?: string; completionDateTime?: string; numberOfUsersTargeted: number; compromisedRate: number; clickedPhishingLinkCount: number; didNotClickLinkCount: number };
export type AttackSimulationData = { configured: boolean; error?: string; total: number; totalTargeted: number; avgCompromiseRate: number; simulations: AttackSim[] };

export interface AlertPolicy {
  id: string;
  name: string;
  enabled: boolean;
  category: "identity" | "devices" | "email" | "compliance" | "licenses";
  condition: string;
  metric: string;
  threshold: number;
  severity: "critical" | "high" | "medium" | "low";
  notifyEmail: string;
  suppressionMinutes?: number;
  createdAt: string;
  lastTriggered?: string;
  triggerCount: number;
}

export interface TriggeredAlert {
  id: string;
  policyId: string;
  policyName: string;
  severity: string;
  category: string;
  condition: string;
  metricValue: number;
  threshold: number;
  triggeredAt: string;
  status: "new" | "acknowledged" | "snoozed" | "auto_resolved" | "resolved";
  acknowledgedAt?: string;
  acknowledgedBy?: string;
  snoozedUntil?: string;
  snoozedBy?: string;
  belowThresholdStreakCount?: number;
  lastEvaluatedAt?: string;
  affectedEntities?: string;
}

export interface NotificationSettings {
  teamsEnabled: boolean; teamsWebhookUrl?: string;
  emailEnabled: boolean; smtpHost?: string; smtpPort: number; smtpUseSsl: boolean;
  smtpUsername?: string; smtpPassword?: string; hasSmtpPassword?: boolean;
  fromAddress?: string; defaultRecipient?: string;
  webhookEnabled: boolean; webhookUrl?: string;
  minSeverity: string;
}

export interface NotificationLogEntry {
  id: number; triggeredAlertId: string; policyName: string;
  channel: string; target?: string; success: boolean; error?: string; sentAt: string;
}

export type AppRole = "Admin" | "Analyst" | "Viewer";
export interface AuthInfo {
  email: string;
  name: string;
  role: AppRole;
  isAdmin: boolean;
  canMutate: boolean;
}

export interface SecurityRecommendation {
  id: string;
  category: string;
  title: string;
  severity: "critical" | "high" | "medium" | "low" | string;
  affectedCount: number;
  whyItMatters: string;
  remediationSteps: string[];
  portalBladeName: string;
  portalDeepLink: string;
}

export interface AlertBaselineRule {
  id: string;
  title: string;
  category: string;
  severity: string;
  description: string;
  isActive: boolean;
  ruleType: "Vigil365" | "NativeM365" | string;
  metric?: string;
  defaultThreshold?: number;
  nativePortalBlade?: string;
  nativePortalDeepLink?: string;
}

export interface AlertCoverageScorecard {
  totalRules: number;
  activeRules: number;
  coveragePercentage: number;
  rules: AlertBaselineRule[];
}
