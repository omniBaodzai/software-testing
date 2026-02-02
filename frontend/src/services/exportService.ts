import api from './api';

export interface ExportRequest {
  analysisId: string;
  format: 'pdf' | 'csv' | 'json';
  includeImages?: boolean;
  includeRecommendations?: boolean;
}

/**
 * Hình dạng response thực tế từ backend (ExportResponseDto)
 * API .NET dùng camelCase nên:
 *  - ExportId          -> exportId
 *  - AnalysisResultId  -> analysisResultId
 *  - ReportType        -> reportType (PDF/CSV/JSON)
 *  - FileName          -> fileName
 *  - FileUrl           -> fileUrl
 *  - FileSize          -> fileSize
 *  - ExportedAt        -> exportedAt
 *  - ExpiresAt         -> expiresAt
 *  - DownloadCount     -> downloadCount
 *  - Status            -> status (Available/Expired)
 */
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

export interface BatchExportRequest {
  analysisIds: string[];
  format: 'pdf' | 'csv' | 'json';
  includeImages?: boolean;
}

const exportService = {
  /**
   * Export analysis result to PDF
   */
  async exportToPdf(analysisId: string): Promise<ExportHistoryItem> {
    const response = await api.post<ExportHistoryItem>(
      `/analysis/${analysisId}/export/pdf`,
      {},
      { timeout: 60000 } // 60 seconds for PDF generation
    );
    return response.data;
  },

  /**
   * Export analysis result to CSV
   */
  async exportToCsv(analysisId: string): Promise<ExportHistoryItem> {
    const response = await api.post<ExportHistoryItem>(
      `/analysis/${analysisId}/export/csv`,
      {},
      { timeout: 30000 }
    );
    return response.data;
  },

  /**
   * Export analysis result to JSON
   */
  async exportToJson(analysisId: string): Promise<ExportHistoryItem> {
    const response = await api.post<ExportHistoryItem>(
      `/analysis/${analysisId}/export/json`,
      {},
      { timeout: 30000 }
    );
    return response.data;
  },

  /**
   * Batch export multiple analyses
   */
  async batchExport(request: BatchExportRequest): Promise<ExportHistoryItem[]> {
    const response = await api.post<ExportHistoryItem[]>(
      '/analysis/export/batch',
      request,
      { timeout: 120000 } // 2 minutes for batch
    );
    return response.data;
  },

  /**
   * Get export history for current user
   */
  async getExportHistory(limit: number = 20, offset: number = 0): Promise<ExportHistoryItem[]> {
    const response = await api.get<ExportHistoryItem[]>('/analysis/exports', {
      params: { limit, offset },
    });
    return response.data;
  },

  /**
   * Get single export by ID
   */
  async getExportById(exportId: string): Promise<ExportHistoryItem> {
    const response = await api.get<ExportHistoryItem>(`/analysis/exports/${exportId}`);
    return response.data;
  },

  /**
   * Download export file
   */
  async downloadExport(exportId: string): Promise<Blob> {
    try {
      const response = await api.get(`/analysis/exports/${exportId}/download`, {
        responseType: 'blob',
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

      const contentType = response.headers['content-type'] || '';
      if (contentType.includes('application/json')) {
        try {
          const text = await response.data.text();
          const errorData = JSON.parse(text);
          throw new Error(errorData.message || 'Không thể tải file');
        } catch {
          throw new Error('Server trả về lỗi không hợp lệ');
        }
      }

      if (!(response.data instanceof Blob)) {
        throw new Error('Response không phải là file hợp lệ');
      }
      if (response.data.size === 0) {
        throw new Error('File download trống');
      }

      return response.data;
    } catch (error: any) {
      if (error.response) {
        const status = error.response.status;
        if (error.response.data instanceof Blob) {
          try {
            const text = await error.response.data.text();
            const errorData = JSON.parse(text);
            throw new Error(errorData.message || `Lỗi ${status}: Không thể tải file`);
          } catch {
            throw new Error(`Lỗi ${status}: Không thể tải file từ server`);
          }
        }
        if (error.response.data && typeof error.response.data === 'object') {
          const errorMsg = error.response.data.message || `Lỗi ${status}: Không thể tải file`;
          throw new Error(errorMsg);
        }
        throw new Error(`Lỗi ${status}: ${error.message || 'Không thể tải file'}`);
      }
      if (error.message) throw error;
      throw new Error('Không thể kết nối đến server để tải file');
    }
  },

  /**
   * Track download (for analytics)
   */
  async trackDownload(exportId: string): Promise<void> {
    await api.post(`/analysis/exports/${exportId}/track-download`);
  },

  /**
   * Helper: Download file directly
   */
  downloadFile(blob: Blob, fileName: string): void {
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    window.URL.revokeObjectURL(url);
  },

  /**
   * Helper: Get file extension from format
   */
  getFileExtension(format: string): string {
    switch (format.toLowerCase()) {
      case 'pdf':
        return '.pdf';
      case 'csv':
        return '.csv';
      case 'json':
        return '.json';
      default:
        return '';
    }
  },

  /**
   * Helper: Format file size
   */
  formatFileSize(bytes?: number): string {
    if (!bytes) return 'N/A';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  },
};

export default exportService;
