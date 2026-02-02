import axios from "axios";
import clinicAuthService from "./clinicAuthService";

const API_BASE_URL = import.meta.env.VITE_API_URL || "/api";

// Clinic-scoped axios instance (uses clinic_token, not patient token)
const clinicApi = axios.create({
  baseURL: API_BASE_URL,
  withCredentials: true,
});

clinicApi.interceptors.request.use((config) => {
  const token = clinicAuthService.getToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

export interface ClinicBulkUploadResponse {
  batchId: string;
  totalFiles: number;
  successCount: number;
  failedCount: number;
  successfullyUploaded: Array<{
    id: string;
    originalFilename: string;
    cloudinaryUrl: string;
    fileSize: number;
    imageType: string;
    uploadStatus: string;
    uploadedAt: string;
  }>;
  failed: Array<{
    filename: string;
    errorMessage: string;
  }>;
  analysisJobId?: string;
  uploadedAt: string;
}

export interface BatchAnalysisStatus {
  jobId: string;
  batchId: string;
  status: "Queued" | "Processing" | "Completed" | "Failed";
  totalImages: number;
  processedCount: number;
  successCount: number;
  failedCount: number;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  imageIds: string[];
}

export interface ClinicAnalysisResult {
  id: string;
  imageId: string;
  analysisStatus: string;
  overallRiskLevel?: "Low" | "Medium" | "High" | "Critical";
  riskScore?: number;
  hypertensionRisk?: "Low" | "Medium" | "High";
  hypertensionScore?: number;
  diabetesRisk?: "Low" | "Medium" | "High";
  diabetesScore?: number;
  diabeticRetinopathyDetected: boolean;
  diabeticRetinopathySeverity?: string;
  strokeRisk?: "Low" | "Medium" | "High";
  strokeScore?: number;
  vesselTortuosity?: number;
  vesselWidthVariation?: number;
  microaneurysmsCount: number;
  hemorrhagesDetected: boolean;
  exudatesDetected: boolean;
  annotatedImageUrl?: string;
  heatmapUrl?: string;
  aiConfidenceScore?: number;
  recommendations?: string;
  healthWarnings?: string;
  processingTimeSeconds?: number;
  analysisStartedAt?: string;
  analysisCompletedAt?: string;
  detailedFindings?: Record<string, any>;
}

/** Lịch Sử Phân Tích - item có thêm patientName */
export interface ClinicAnalysisReportItem extends ClinicAnalysisResult {
  patientName?: string | null;
  patientUserId?: string | null;
}

export interface QueueAnalysisRequest {
  imageIds: string[];
  batchId?: string;
}

/** Giống patient: response từ POST analysis/start */
export interface ClinicAnalysisStartResponse {
  analysisId: string;
  imageId: string;
  status: "Processing" | "Completed" | "Failed";
  startedAt?: string;
  completedAt?: string;
}

const clinicImageService = {
  /**
   * Bắt đầu phân tích (giống patient). Gọi sau khi upload xong, có imageIds.
   * Trả về analysisId để redirect sang trang kết quả.
   */
  async startAnalysis(request: { imageIds: string[] }): Promise<ClinicAnalysisStartResponse | ClinicAnalysisStartResponse[]> {
    const response = await clinicApi.post<ClinicAnalysisStartResponse | ClinicAnalysisStartResponse[]>(
      "clinic/analysis/start",
      request,
      { timeout: 90000 }
    );
    return response.data;
  },

  /**
   * Lấy kết quả phân tích theo analysisId (giống patient GET /analysis/:id).
   */
  async getAnalysisResult(analysisId: string): Promise<ClinicAnalysisResult> {
    const response = await clinicApi.get<ClinicAnalysisResult>(
      `clinic/analysis/result/${analysisId}`
    );
    return response.data;
  },

  /**
   * Bulk upload retinal images for clinic (FR-24)
   * @param files Array of File objects
   * @param options Upload options
   */
  async bulkUploadImages(
    files: File[],
    options?: {
      patientUserId?: string;
      doctorId?: string;
      batchName?: string;
      autoStartAnalysis?: boolean;
      imageType?: string;
      eyeSide?: string;
      captureDevice?: string;
      captureDate?: string;
    }
  ): Promise<ClinicBulkUploadResponse> {
    const formData = new FormData();

    // Append all files
    files.forEach((file) => {
      formData.append("files", file);
    });

    // Append options
    if (options?.patientUserId) {
      formData.append("patientUserId", options.patientUserId);
    }
    if (options?.doctorId) {
      formData.append("doctorId", options.doctorId);
    }
    if (options?.batchName) {
      formData.append("batchName", options.batchName);
    }
    if (options?.autoStartAnalysis !== undefined) {
      formData.append("autoStartAnalysis", options.autoStartAnalysis.toString());
    }
    if (options?.imageType) {
      formData.append("imageType", options.imageType);
    }
    if (options?.eyeSide) {
      formData.append("eyeSide", options.eyeSide);
    }
    if (options?.captureDevice) {
      formData.append("captureDevice", options.captureDevice);
    }
    if (options?.captureDate) {
      formData.append("captureDate", options.captureDate);
    }

    const response = await clinicApi.post<ClinicBulkUploadResponse>(
      "clinic/images/bulk-upload",
      formData,
      {
        headers: {
          "Content-Type": "multipart/form-data",
        },
        timeout: 300000, // 5 minutes timeout for large uploads
      }
    );

    return response.data;
  },

  /**
   * Get status of a batch analysis job
   */
  async getBatchAnalysisStatus(jobId: string): Promise<BatchAnalysisStatus> {
    const response = await clinicApi.get<BatchAnalysisStatus>(
      `clinic/images/analysis/${jobId}/status`
    );
    return response.data;
  },

  /**
   * Get analysis results for a job (clinic scope)
   */
  async getBatchAnalysisResults(jobId: string): Promise<ClinicAnalysisResult[]> {
    const response = await clinicApi.get<ClinicAnalysisResult[]>(
      `clinic/images/analysis/${jobId}/results`
    );
    return response.data;
  },

  /**
   * Queue a batch of already-uploaded images for AI analysis
   */
  async queueBatchAnalysis(
    request: QueueAnalysisRequest
  ): Promise<BatchAnalysisStatus> {
    const response = await clinicApi.post<BatchAnalysisStatus>(
      "clinic/images/queue-analysis",
      request
    );
    return response.data;
  },

  /**
   * Get status of a bulk upload batch
   */
  async getBatchStatus(batchId: string): Promise<BulkUploadBatchStatus> {
    const response = await clinicApi.get<BulkUploadBatchStatus>(
      `clinic/images/batches/${batchId}/status`
    );
    return response.data;
  },

  /**
   * Lấy tất cả kết quả phân tích của phòng khám (Lịch Sử Phân Tích - giống user reports)
   */
  async getClinicAnalysisReports(): Promise<ClinicAnalysisReportItem[]> {
    const response = await clinicApi.get<ClinicAnalysisReportItem[]>(
      "clinic/images/analyses"
    );
    return response.data;
  },

  /**
   * List recent analysis jobs for the clinic (dashboard)
   */
  async getAnalysisJobs(limit = 10): Promise<BatchAnalysisStatus[]> {
    const response = await clinicApi.get<BatchAnalysisStatus[]>(
      `clinic/images/analysis/jobs?limit=${limit}`
    );
    return response.data;
  },

  /**
   * List all bulk upload batches for the clinic
   */
  async listBatches(options?: {
    page?: number;
    pageSize?: number;
    status?: string;
  }): Promise<BulkUploadBatchStatus[]> {
    const params = new URLSearchParams();
    if (options?.page) params.append("page", options.page.toString());
    if (options?.pageSize) params.append("pageSize", options.pageSize.toString());
    if (options?.status) params.append("status", options.status);

    const response = await clinicApi.get<BulkUploadBatchStatus[]>(
      `clinic/images/batches?${params.toString()}`
    );
    return response.data;
  },
};

export interface BulkUploadBatchStatus {
  batchId: string;
  clinicId: string;
  uploadedBy: string;
  uploadedByType: string;
  batchName?: string;
  totalImages: number;
  processedImages: number;
  failedImages: number;
  processingImages: number;
  uploadStatus: "Pending" | "Uploading" | "Processing" | "Completed" | "Failed" | "PartiallyCompleted";
  startedAt?: string;
  completedAt?: string;
  failureReason?: string;
  metadata?: string;
  createdDate?: string;
}

export default clinicImageService;

