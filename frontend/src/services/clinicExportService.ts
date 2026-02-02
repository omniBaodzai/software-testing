import axios from "axios";
import clinicAuthService from "./clinicAuthService";

const clinicExportApi = axios.create({
  baseURL: import.meta.env.VITE_API_URL || "/api",
  withCredentials: true,
});

clinicExportApi.interceptors.request.use((config) => {
  const token = clinicAuthService.getToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

export interface ExportHistoryItem {
  exportId: string;
  analysisResultId?: string | null;
  reportType: string;
  fileUrl?: string;
  fileName?: string;
  fileSize?: number;
  downloadCount: number;
  exportedAt: string;
  expiresAt?: string | null;
  status?: string;
}

const clinicExportService = {
  async exportToPdf(analysisId: string): Promise<ExportHistoryItem> {
    const response = await clinicExportApi.post<ExportHistoryItem>(
      `clinic/analysis/result/${analysisId}/export/pdf`,
      {},
      { timeout: 60000 }
    );
    return response.data;
  },

  async exportToCsv(analysisId: string): Promise<ExportHistoryItem> {
    const response = await clinicExportApi.post<ExportHistoryItem>(
      `clinic/analysis/result/${analysisId}/export/csv`,
      {},
      { timeout: 30000 }
    );
    return response.data;
  },

  async exportToJson(analysisId: string): Promise<ExportHistoryItem> {
    const response = await clinicExportApi.post<ExportHistoryItem>(
      `clinic/analysis/result/${analysisId}/export/json`,
      {},
      { timeout: 30000 }
    );
    return response.data;
  },

  async downloadExport(exportId: string): Promise<Blob> {
    const response = await clinicExportApi.get(`clinic/analysis/exports/${exportId}/download`, {
      responseType: "blob",
      validateStatus: (status) => status < 500,
    });

    if (response.status !== 200) {
      try {
        const text = await response.data.text();
        const errorData = JSON.parse(text);
        throw new Error(errorData.message || `Lỗi ${response.status}: Không thể tải file`);
      } catch {
        throw new Error(`Lỗi ${response.status}: Không thể tải file`);
      }
    }

    const contentType = response.headers["content-type"] || "";
    if (contentType.includes("application/json")) {
      try {
        const text = await response.data.text();
        const errorData = JSON.parse(text);
        throw new Error(errorData.message || "Không thể tải file");
      } catch {
        throw new Error("Server trả về lỗi không hợp lệ");
      }
    }

    if (!(response.data instanceof Blob) || response.data.size === 0) {
      throw new Error("File download trống");
    }

    return response.data;
  },

  downloadFile(blob: Blob, fileName: string): void {
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    window.URL.revokeObjectURL(url);
  },
};

export default clinicExportService;
