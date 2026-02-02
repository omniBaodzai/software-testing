import axios from "axios";
import clinicAuthService from "./clinicAuthService";

// Re-export types from alertService for clinic page
export type {
  HighRiskAlert,
  ClinicAlertSummary,
  AbnormalTrend,
  PatientRiskTrend,
  RiskTrendPoint,
} from "./alertService";

const API_BASE_URL = import.meta.env.VITE_API_URL || "/api";

const clinicAlertsApi = axios.create({
  baseURL: API_BASE_URL,
  timeout: 30000,
  headers: { "Content-Type": "application/json" },
});

clinicAlertsApi.interceptors.request.use((config) => {
  const token = clinicAuthService.getToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

// Import types for return type annotations
import type { HighRiskAlert, ClinicAlertSummary, AbnormalTrend } from "./alertService";

const clinicAlertService = {
  async getClinicAlertSummary(): Promise<ClinicAlertSummary> {
    const response = await clinicAlertsApi.get<ClinicAlertSummary>("alerts/clinic/summary");
    return response.data;
  },

  async getClinicAlerts(
    unacknowledgedOnly: boolean = false,
    limit: number = 50
  ): Promise<HighRiskAlert[]> {
    const response = await clinicAlertsApi.get<HighRiskAlert[]>("alerts/clinic", {
      params: { unacknowledgedOnly, limit },
    });
    return response.data;
  },

  async detectAbnormalTrends(days: number = 30): Promise<AbnormalTrend[]> {
    const response = await clinicAlertsApi.get<AbnormalTrend[]>("alerts/clinic/abnormal-trends", {
      params: { days },
    });
    return response.data;
  },

  async acknowledgeAlert(alertId: string): Promise<void> {
    await clinicAlertsApi.post(`alerts/${alertId}/acknowledge`);
  },
};

export default clinicAlertService;
