import adminApi from "./adminApi";

export interface ComplianceReport {
  totalAuditLogs: number;
  logsLast30Days: number;
  logsLast7Days: number;
  uniqueUsers: number;
  uniqueAdmins: number;
  actionTypeCounts: Record<string, number>;
  resourceTypeCounts: Record<string, number>;
  issues: ComplianceIssue[];
}

export interface ComplianceIssue {
  issueType: string;
  description: string;
  severity: string;
  count: number;
  lastOccurrence?: string;
}

export interface PrivacySettings {
  enableAuditLogging: boolean;
  auditLogRetentionDays: number;
  anonymizeOldLogs: boolean;
  requireConsentForDataSharing: boolean;
  enableGdprCompliance: boolean;
  dataRetentionDays: number;
  allowDataExport: boolean;
  requireTwoFactorForSensitiveActions: boolean;
}

export interface UpdatePrivacySettingsDto {
  enableAuditLogging?: boolean;
  auditLogRetentionDays?: number;
  anonymizeOldLogs?: boolean;
  requireConsentForDataSharing?: boolean;
  enableGdprCompliance?: boolean;
  dataRetentionDays?: number;
  allowDataExport?: boolean;
  requireTwoFactorForSensitiveActions?: boolean;
}

const complianceApi = {
  getReport: async (
    startDate?: string,
    endDate?: string
  ): Promise<ComplianceReport> => {
    const params: any = {};
    if (startDate) params.startDate = startDate;
    if (endDate) params.endDate = endDate;

    const res = await adminApi.get("/admin/compliance/report", { params });
    return res.data;
  },

  getPrivacySettings: async (): Promise<PrivacySettings> => {
    const res = await adminApi.get("/admin/compliance/privacy-settings");
    return res.data;
  },

  updatePrivacySettings: async (
    dto: UpdatePrivacySettingsDto
  ): Promise<void> => {
    await adminApi.put("/admin/compliance/privacy-settings", dto);
  },

  createSampleLogs: async (): Promise<{ message: string; count: number }> => {
    const res = await adminApi.post(
      "/admin/compliance/test/create-sample-logs"
    );
    return res.data;
  },
};

export default complianceApi;
