import { useState, useEffect } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import ClinicHeader from "../../components/clinic/ClinicHeader";
import clinicAuthService from "../../services/clinicAuthService";
import clinicImageService, { ClinicAnalysisResult } from "../../services/clinicImageService";
import AnalysisResultDisplay from "../../components/analysis/AnalysisResultDisplay";
import clinicExportService from "../../services/clinicExportService";
import type { AnalysisResult } from "../../services/analysisService";
import toast from "react-hot-toast";
import { getApiErrorMessage } from "../../utils/getApiErrorMessage";

const mapToAnalysisResult = (r: ClinicAnalysisResult): AnalysisResult => ({
  id: r.id,
  imageId: r.imageId,
  analysisStatus: r.analysisStatus,
  overallRiskLevel: r.overallRiskLevel,
  riskScore: r.riskScore,
  hypertensionRisk: r.hypertensionRisk,
  hypertensionScore: r.hypertensionScore,
  diabetesRisk: r.diabetesRisk,
  diabetesScore: r.diabetesScore,
  diabeticRetinopathyDetected: r.diabeticRetinopathyDetected,
  diabeticRetinopathySeverity: r.diabeticRetinopathySeverity,
  strokeRisk: r.strokeRisk,
  strokeScore: r.strokeScore,
  vesselTortuosity: r.vesselTortuosity,
  vesselWidthVariation: r.vesselWidthVariation,
  microaneurysmsCount: r.microaneurysmsCount,
  hemorrhagesDetected: r.hemorrhagesDetected,
  exudatesDetected: r.exudatesDetected,
  annotatedImageUrl: r.annotatedImageUrl,
  heatmapUrl: r.heatmapUrl,
  aiConfidenceScore: r.aiConfidenceScore,
  recommendations: r.recommendations,
  healthWarnings: r.healthWarnings,
  processingTimeSeconds: r.processingTimeSeconds,
  analysisStartedAt: r.analysisStartedAt,
  analysisCompletedAt: r.analysisCompletedAt,
  detailedFindings: r.detailedFindings,
});

/**
 * Trang xem kết quả phân tích AI theo analysisId (clinic).
 * Luồng giống patient: vào từ Upload → Bắt đầu phân tích → redirect /clinic/analysis/result/:analysisId.
 * Gọi GET clinic/analysis/result/:analysisId với retry khi 404 (AI đang xử lý).
 */
const ClinicAnalysisResultPage = () => {
  const { analysisId } = useParams<{ analysisId: string }>();
  const navigate = useNavigate();
  const [result, setResult] = useState<ClinicAnalysisResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [retrying, setRetrying] = useState(false);

  useEffect(() => {
    (async () => {
      const ok = await clinicAuthService.ensureLoggedIn();
      if (!ok) {
        navigate("/login");
        return;
      }
    })();
  }, [navigate]);

  const loadAnalysisResult = async () => {
    if (!analysisId) return;
    const data = await clinicImageService.getAnalysisResult(analysisId);
    setResult(data);
  };

  const loadAnalysisResultWithRetry = async () => {
    if (!analysisId) return;

    const maxAttempts = 10;
    let attempt = 0;

    setRetrying(true);
    setResult(null);
    setLoading(true);

    while (attempt < maxAttempts) {
      try {
        await loadAnalysisResult();
        setRetrying(false);
        setLoading(false);
        return;
      } catch (error: any) {
        const status = error?.response?.status;

        if (status === 404) {
          attempt += 1;
          const delayMs = Math.min(3000, 800 + attempt * 250);
          await new Promise((r) => setTimeout(r, delayMs));
          continue;
        }

        toast.error(
          `Lỗi khi tải kết quả phân tích: ${getApiErrorMessage(error, "Không thể tải kết quả")}`
        );
        setRetrying(false);
        setLoading(false);
        return;
      }
    }

    toast.error("Kết quả phân tích chưa sẵn sàng. Vui lòng thử tải lại sau.");
    setRetrying(false);
    setLoading(false);
  };

  useEffect(() => {
    if (!analysisId) {
      toast.error("Mã kết quả không hợp lệ");
      navigate("/clinic/dashboard");
      return;
    }
    loadAnalysisResultWithRetry();
  }, [analysisId]);

  if (loading) {
    return (
      <div className="min-h-screen bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50">
        <ClinicHeader />
        <div className="flex-grow flex items-center justify-center py-16">
          <div className="text-center">
            <span className="material-symbols-outlined animate-spin text-5xl text-blue-600 dark:text-blue-400">
              progress_activity
            </span>
            <p className="mt-4 text-slate-600 dark:text-slate-400">
              {retrying ? "Đang chờ hệ thống xử lý..." : "Đang tải kết quả phân tích..."}
            </p>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-500">
              Có thể mất 15–30 giây
            </p>
          </div>
        </div>
      </div>
    );
  }

  if (!result) {
    return (
      <div className="min-h-screen bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50">
        <ClinicHeader />
        <div className="flex-grow flex items-center justify-center py-16">
          <div className="text-center max-w-md mx-auto px-4">
            <span className="material-symbols-outlined text-5xl text-amber-500 dark:text-amber-400">
              hourglass_top
            </span>
            <p className="mt-4 text-slate-600 dark:text-slate-400">
              Chưa có kết quả phân tích. AI có thể đang xử lý.
            </p>
            <div className="mt-6 flex flex-wrap gap-3 justify-center">
              <button
                type="button"
                onClick={() => loadAnalysisResultWithRetry()}
                className="px-6 py-2.5 bg-blue-600 hover:bg-blue-700 text-white rounded-lg font-semibold transition-colors"
              >
                Thử tải lại
              </button>
              <Link
                to="/clinic/dashboard"
                className="px-6 py-2.5 bg-white dark:bg-slate-800 border-2 border-slate-300 dark:border-slate-600 text-slate-700 dark:text-slate-300 rounded-lg hover:bg-slate-50 dark:hover:bg-slate-700 font-semibold transition-colors"
              >
                Về Tổng quan
              </Link>
              <Link
                to="/clinic/upload"
                className="px-6 py-2.5 bg-slate-200 dark:bg-slate-700 text-slate-800 dark:text-slate-200 rounded-lg font-semibold hover:bg-slate-300 dark:hover:bg-slate-600"
              >
                Upload &amp; phân tích mới
              </Link>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50">
      <ClinicHeader />
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
            Kết quả phân tích AI
          </h1>
          <div className="flex gap-3">
            <Link
              to="/clinic/dashboard"
              className="px-4 py-2 rounded-lg border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-800 text-slate-700 dark:text-slate-300 font-medium hover:bg-slate-50 dark:hover:bg-slate-700"
            >
              Tổng quan
            </Link>
            <Link
              to="/clinic/upload"
              className="px-4 py-2 rounded-lg bg-indigo-600 text-white font-medium hover:bg-indigo-700"
            >
              Upload &amp; phân tích mới
            </Link>
          </div>
        </div>
        <AnalysisResultDisplay
          result={mapToAnalysisResult(result)}
          showExport={true}
          exportService={clinicExportService}
          exportHistoryLink="/clinic/reports"
        />
      </main>
    </div>
  );
};

export default ClinicAnalysisResultPage;
