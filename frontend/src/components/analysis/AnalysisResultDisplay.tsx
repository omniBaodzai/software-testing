import { useState } from 'react';
import { Link } from 'react-router-dom';
import { AnalysisResult } from '../../services/analysisService';
import exportService from '../../services/exportService';
import doctorService from '../../services/doctorService';
import { useAuthStore } from '../../store/authStore';
import toast from 'react-hot-toast';

interface AnalysisResultDisplayProps {
  result: AnalysisResult;
  onValidated?: () => void;
  /** Ẩn phần xuất báo cáo (dùng khi hiển thị từ trang clinic) */
  showExport?: boolean;
}

const AI_CORE_BASE_URL =
  import.meta.env.VITE_AI_CORE_BASE_URL || 'http://localhost:8000';

const resolveImageUrl = (path?: string | null) => {
  if (!path) return undefined;
  if (path.startsWith('http://') || path.startsWith('https://')) return path;
  return `${AI_CORE_BASE_URL}${path}`;
};

const AnalysisResultDisplay = ({ result, onValidated, showExport = true }: AnalysisResultDisplayProps) => {
  const { user } = useAuthStore();
  const isDoctor = user?.userType === 'Doctor';
  const [exporting, setExporting] = useState<string | null>(null);
  
  // FR-15: Validate/Correct findings
  const [showValidateModal, setShowValidateModal] = useState(false);
  const [validating, setValidating] = useState(false);
  const [validationStatus, setValidationStatus] = useState<'Validated' | 'Corrected'>('Validated');
  const [validationNotes, setValidationNotes] = useState('');
  const [correctedRiskLevel, setCorrectedRiskLevel] = useState<string>(result.overallRiskLevel || 'Low');
  
  // FR-19: AI Feedback
  const [showFeedbackModal, setShowFeedbackModal] = useState(false);
  const [submittingFeedback, setSubmittingFeedback] = useState(false);
  const [feedbackType, setFeedbackType] = useState<'Correct' | 'Incorrect' | 'PartiallyCorrect' | 'NeedsReview'>('Correct');
  const [feedbackNotes, setFeedbackNotes] = useState('');
  const [feedbackRating, setFeedbackRating] = useState(5);

  // FR-15: Handle validate findings
  const handleValidateFindings = async () => {
    try {
      setValidating(true);
      await doctorService.validateAnalysis(result.id, {
        isAccurate: validationStatus === 'Validated',
        doctorNotes: validationNotes,
        correctedRiskLevel: validationStatus === 'Corrected' ? correctedRiskLevel : undefined,
      });
      toast.success(validationStatus === 'Validated' ? 'Đã xác nhận kết quả phân tích' : 'Đã sửa và lưu kết quả');
      setShowValidateModal(false);
      onValidated?.();
    } catch (error: any) {
      console.error('Error validating:', error);
      toast.error(error?.response?.data?.message || 'Không thể xác nhận kết quả');
    } finally {
      setValidating(false);
    }
  };

  // FR-19: Handle submit AI feedback
  const handleSubmitFeedback = async () => {
    try {
      setSubmittingFeedback(true);
      await doctorService.submitAiFeedback({
        analysisId: result.id,
        feedbackType,
        feedbackContent: feedbackNotes,
        rating: feedbackRating,
      });
      toast.success('Đã gửi phản hồi AI thành công');
      setShowFeedbackModal(false);
      setFeedbackNotes('');
    } catch (error: any) {
      console.error('Error submitting feedback:', error);
      toast.error(error?.response?.data?.message || 'Không thể gửi phản hồi');
    } finally {
      setSubmittingFeedback(false);
    }
  };

  const handleExport = async (format: 'pdf' | 'csv' | 'json') => {
    try {
      setExporting(format);
      toast.loading(`Đang tạo báo cáo ${format.toUpperCase()}...`, { id: `export-${format}` });
      
      let exportResult;
      
      switch (format) {
        case 'pdf':
          exportResult = await exportService.exportToPdf(result.id);
          break;
        case 'csv':
          exportResult = await exportService.exportToCsv(result.id);
          break;
        case 'json':
          exportResult = await exportService.exportToJson(result.id);
          break;
      }

      if (!exportResult) {
        throw new Error('Không nhận được kết quả từ server');
      }

      // Download file từ backend endpoint (không mở trực tiếp từ Cloudinary URL)
      try {
        // Backend trả về exportId, không phải id
        const exportId = (exportResult as any).exportId ?? (exportResult as any).id;
        if (!exportId) {
          throw new Error('Không tìm thấy mã báo cáo (exportId) trong phản hồi từ server');
        }

        const blob = await exportService.downloadExport(exportId);
        if (!blob || blob.size === 0) {
          throw new Error('File download trống hoặc không hợp lệ');
        }
        // Get current date in Vietnam timezone (UTC+7)
        const now = new Date();
        const vnDate = new Date(now.toLocaleString('en-US', { timeZone: 'Asia/Ho_Chi_Minh' }));
        const dateStr = vnDate.toISOString().split('T')[0];
        const fileName = exportResult.fileName || `aura_report_${result.id.substring(0, 8)}_${dateStr}.${format}`;
        exportService.downloadFile(blob, fileName);
        toast.success(`✅ Xuất ${format.toUpperCase()} thành công!`, { id: `export-${format}` });
      } catch (downloadError: any) {
        // Fallback: file đã lên Cloudinary thì tải trực tiếp từ fileUrl
        const fileUrl = (exportResult as any).fileUrl;
        if (fileUrl) {
          const fileName = exportResult.fileName || `aura_report_${result.id.substring(0, 8)}.${format}`;
          const link = document.createElement('a');
          link.href = fileUrl;
          link.download = fileName;
          link.target = '_blank';
          link.rel = 'noopener noreferrer';
          document.body.appendChild(link);
          link.click();
          document.body.removeChild(link);
          toast.success(`✅ Xuất ${format.toUpperCase()} thành công!`, { id: `export-${format}` });
          return;
        }
        let errorMsg = 'Không thể tải file';
        if (downloadError?.message) errorMsg = downloadError.message;
        else if (downloadError?.response?.data?.message) errorMsg = downloadError.response.data.message;
        toast.error(`❌ Không thể tải ${format.toUpperCase()}: ${errorMsg}. Vui lòng thử lại hoặc kiểm tra lịch sử xuất báo cáo`, { id: `export-${format}`, duration: 5000 });
      }
    } catch (error: any) {
      const errorMessage = error?.response?.data?.message || error?.message || 'Không thể kết nối đến server';
      toast.error(`❌ Không thể xuất ${format.toUpperCase()}: ${errorMessage}`, { id: `export-${format}`, duration: 5000 });
    } finally {
      setExporting(null);
    }
  };

  const getRiskColor = (risk?: string) => {
    switch (risk) {
      case 'Low':
        return 'text-green-600 dark:text-green-400 bg-green-50 dark:bg-green-900/20';
      case 'Medium':
        return 'text-amber-600 dark:text-amber-400 bg-amber-50 dark:bg-amber-900/20';
      case 'High':
      case 'Critical':
        return 'text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-900/20';
      default:
        return 'text-gray-600 dark:text-gray-400 bg-gray-50 dark:bg-gray-900/20';
    }
  };

  const getRiskLabel = (risk?: string) => {
    switch (risk) {
      case 'Low':
        return 'Thấp';
      case 'Medium':
        return 'Trung bình';
      case 'High':
        return 'Cao';
      case 'Critical':
        return 'Nghiêm trọng';
      default:
        return 'Chưa xác định';
    }
  };

  return (
    <main className="flex-grow w-full max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 md:py-12">
      {/* Header */}
      <div className="mb-8 md:mb-10">
        <div className="flex flex-col gap-3">
          <h1 className="text-3xl md:text-4xl lg:text-5xl font-black leading-tight tracking-tight text-slate-900 dark:text-white">
            Kết quả Phân tích AI
          </h1>
          <p className="text-slate-600 dark:text-slate-400 text-base md:text-lg">
            Phân tích được thực hiện vào{' '}
            <span className="font-semibold text-slate-700 dark:text-slate-300">
              {result.analysisCompletedAt
                ? new Date(result.analysisCompletedAt).toLocaleString('vi-VN', {
                    timeZone: 'Asia/Ho_Chi_Minh',
                    year: 'numeric',
                    month: 'long',
                    day: 'numeric',
                    hour: '2-digit',
                    minute: '2-digit',
                  })
                : 'Chưa hoàn thành'}
            </span>
          </p>
        </div>
      </div>

      {/* Overall Risk Card */}
      <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 md:p-8 mb-6">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
          <h2 className="text-2xl font-bold text-slate-900 dark:text-white">
            Đánh giá Tổng thể
          </h2>
          <span
            className={`px-5 py-2.5 rounded-lg font-bold text-base ${getRiskColor(result.overallRiskLevel)}`}
          >
            {getRiskLabel(result.overallRiskLevel)}
          </span>
        </div>
        {result.riskScore !== undefined && (
          <div className="mb-6">
            <div className="flex justify-between text-sm font-semibold mb-3">
              <span className="text-slate-600 dark:text-slate-400">Điểm rủi ro</span>
              <span className="text-slate-900 dark:text-white text-lg">{result.riskScore}/100</span>
            </div>
            <div className="h-4 w-full bg-slate-200 dark:bg-slate-700 rounded-full overflow-hidden">
              <div
                className={`h-full rounded-full transition-all duration-500 ${
                  result.riskScore < 40
                    ? 'bg-green-500'
                    : result.riskScore < 70
                    ? 'bg-amber-500'
                    : 'bg-red-500'
                }`}
                style={{ width: `${result.riskScore}%` }}
              />
            </div>
          </div>
        )}
        {result.aiConfidenceScore !== undefined && (
          <div className="text-sm text-slate-600 dark:text-slate-400">
            Độ tin cậy AI: <span className="font-bold text-slate-900 dark:text-white">{Number(result.aiConfidenceScore).toFixed(2)}%</span>
          </div>
        )}
      </div>

      {/* Risk Assessments Grid */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-6">
        {/* Cardiovascular Risk */}
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 hover:shadow-md transition-shadow">
          <div className="flex items-center gap-3 mb-4">
            <div className="size-12 rounded-full bg-rose-50 dark:bg-rose-900/30 flex items-center justify-center">
              <span className="material-symbols-outlined text-rose-500 dark:text-rose-400 text-2xl">favorite</span>
            </div>
            <h3 className="font-bold text-lg text-slate-900 dark:text-white">
              Tim mạch
            </h3>
          </div>
          <div className={`inline-block px-4 py-2 rounded-lg text-sm font-semibold mb-3 ${getRiskColor(result.hypertensionRisk)}`}>
            {getRiskLabel(result.hypertensionRisk)}
          </div>
          {result.hypertensionScore !== undefined && (
            <p className="text-sm text-slate-600 dark:text-slate-400">
              Điểm: <span className="font-bold text-slate-900 dark:text-white">{result.hypertensionScore}/100</span>
            </p>
          )}
        </div>

        {/* Diabetes Risk */}
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 hover:shadow-md transition-shadow">
          <div className="flex items-center gap-3 mb-4">
            <div className="size-12 rounded-full bg-blue-50 dark:bg-blue-900/30 flex items-center justify-center">
              <span className="material-symbols-outlined text-blue-500 dark:text-blue-400 text-2xl">visibility</span>
            </div>
            <h3 className="font-bold text-lg text-slate-900 dark:text-white">
              Đái tháo đường
            </h3>
          </div>
          <div className={`inline-block px-4 py-2 rounded-lg text-sm font-semibold mb-3 ${getRiskColor(result.diabetesRisk)}`}>
            {getRiskLabel(result.diabetesRisk)}
          </div>
          {result.diabetesScore !== undefined && (
            <p className="text-sm text-slate-600 dark:text-slate-400 mb-2">
              Điểm: <span className="font-bold text-slate-900 dark:text-white">{result.diabetesScore}/100</span>
            </p>
          )}
          {result.diabeticRetinopathyDetected && (
            <p className="text-sm text-amber-600 dark:text-amber-400 mt-2 font-medium flex items-center gap-1">
              <span className="material-symbols-outlined text-base">warning</span>
              Phát hiện võng mạc đái tháo đường
            </p>
          )}
        </div>

        {/* Stroke Risk */}
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 hover:shadow-md transition-shadow">
          <div className="flex items-center gap-3 mb-4">
            <div className="size-12 rounded-full bg-purple-50 dark:bg-purple-900/30 flex items-center justify-center">
              <span className="material-symbols-outlined text-purple-500 dark:text-purple-400 text-2xl">warning</span>
            </div>
            <h3 className="font-bold text-lg text-slate-900 dark:text-white">
              Đột quỵ
            </h3>
          </div>
          <div className={`inline-block px-4 py-2 rounded-lg text-sm font-semibold mb-3 ${getRiskColor(result.strokeRisk)}`}>
            {getRiskLabel(result.strokeRisk)}
          </div>
          {result.strokeScore !== undefined && (
            <p className="text-sm text-slate-600 dark:text-slate-400">
              Điểm: <span className="font-bold text-slate-900 dark:text-white">{result.strokeScore}/100</span>
            </p>
          )}
        </div>
      </div>

      {/* Vascular Abnormalities */}
      <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 md:p-8 mb-6">
        <h2 className="text-2xl font-bold text-slate-900 dark:text-white mb-6">
          Bất thường Mạch máu
        </h2>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-6">
          <div className="flex flex-col gap-2">
            <p className="text-sm font-medium text-slate-600 dark:text-slate-400">Độ xoắn mạch</p>
            <p className="text-2xl font-bold text-slate-900 dark:text-white">
              {result.vesselTortuosity?.toFixed(2) ?? 'N/A'}
            </p>
          </div>
          <div className="flex flex-col gap-2">
            <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
              Biến đổi độ rộng
            </p>
            <p className="text-2xl font-bold text-slate-900 dark:text-white">
              {result.vesselWidthVariation?.toFixed(2) ?? 'N/A'}
            </p>
          </div>
          <div className="flex flex-col gap-2">
            <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
              Vi phình mạch
            </p>
            <p className="text-2xl font-bold text-slate-900 dark:text-white">{result.microaneurysmsCount ?? 0}</p>
          </div>
          <div className="flex flex-col gap-2">
            <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
              Xuất huyết
            </p>
            <p className="text-2xl font-bold text-slate-900 dark:text-white">
              {result.hemorrhagesDetected ? 'Có' : 'Không'}
            </p>
          </div>
        </div>
      </div>

      {/* Annotated Images */}
      {(result.annotatedImageUrl || result.heatmapUrl) && (
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 md:p-8 mb-6">
          <h2 className="text-2xl font-bold text-slate-900 dark:text-white mb-6">
            Hình ảnh Phân tích
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
            {result.annotatedImageUrl && (
              <div className="flex flex-col gap-3">
                <h3 className="text-base font-semibold text-slate-700 dark:text-slate-300">
                  Ảnh đã chú thích
                </h3>
                <div className="rounded-xl overflow-hidden border-2 border-slate-200 dark:border-slate-700 shadow-sm">
                  <img
                    src={resolveImageUrl(result.annotatedImageUrl)}
                    alt="Annotated retinal image"
                    className="w-full h-auto"
                  />
                </div>
              </div>
            )}
            {result.heatmapUrl && (
              <div className="flex flex-col gap-3">
                <h3 className="text-base font-semibold text-slate-700 dark:text-slate-300">
                  Heatmap
                </h3>
                <div className="rounded-xl overflow-hidden border-2 border-slate-200 dark:border-slate-700 shadow-sm">
                  <img
                    src={resolveImageUrl(result.heatmapUrl)}
                    alt="Heatmap visualization"
                    className="w-full h-auto"
                  />
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Recommendations */}
      {result.recommendations && (
        <div className="bg-blue-50 dark:bg-blue-900/20 rounded-2xl border border-blue-200 dark:border-blue-800 p-6 md:p-8 mb-6">
          <h2 className="text-xl font-bold text-slate-900 dark:text-white mb-4 flex items-center gap-3">
            <span className="material-symbols-outlined text-blue-600 dark:text-blue-400 text-2xl">lightbulb</span>
            Khuyến nghị
          </h2>
          <p className="text-slate-700 dark:text-slate-300 leading-relaxed whitespace-pre-line">
            {result.recommendations}
          </p>
        </div>
      )}
      
      {/* Health Warnings */}
      {result.healthWarnings && (
        <div className="bg-amber-50 dark:bg-amber-900/20 rounded-2xl border border-amber-200 dark:border-amber-800 p-6 md:p-8 mb-6">
          <h2 className="text-xl font-bold text-slate-900 dark:text-white mb-4 flex items-center gap-3">
            <span className="material-symbols-outlined text-amber-600 dark:text-amber-400 text-2xl">warning</span>
            Cảnh báo Sức khỏe
          </h2>
          <p className="text-slate-700 dark:text-slate-300 leading-relaxed whitespace-pre-line">
            {result.healthWarnings}
          </p>
        </div>
      )}

      {/* Doctor Actions - FR-15 & FR-19 */}
      {isDoctor && (
        <div className="bg-gradient-to-r from-blue-50 to-indigo-50 dark:from-blue-900/20 dark:to-indigo-900/20 rounded-2xl border border-blue-200 dark:border-blue-800 p-6 md:p-8 mb-6">
          <h2 className="text-xl font-bold text-slate-900 dark:text-white mb-4 flex items-center gap-3">
            <span className="material-symbols-outlined text-blue-600 dark:text-blue-400 text-2xl">verified_user</span>
            Hành động Bác sĩ
          </h2>
          <p className="text-slate-600 dark:text-slate-400 mb-6">
            Xác nhận hoặc sửa đổi kết quả phân tích AI, và gửi phản hồi để cải thiện độ chính xác
          </p>
          <div className="flex flex-wrap gap-3">
            <button
              onClick={() => setShowValidateModal(true)}
              className="flex items-center gap-2 px-5 py-2.5 bg-blue-600 hover:bg-blue-700 text-white rounded-lg font-medium transition-colors"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              Xác nhận / Sửa kết quả
            </button>
            <button
              onClick={() => setShowFeedbackModal(true)}
              className="flex items-center gap-2 px-5 py-2.5 bg-purple-600 hover:bg-purple-700 text-white rounded-lg font-medium transition-colors"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z" />
              </svg>
              Gửi Phản hồi AI
            </button>
          </div>
        </div>
      )}

      {/* Export Actions - ẩn khi dùng từ clinic (showExport=false) */}
      {showExport && (
      <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 md:p-8 mb-6">
        <h2 className="text-xl font-bold text-slate-900 dark:text-white mb-4">
          Xuất Báo cáo
        </h2>
        <p className="text-slate-600 dark:text-slate-400 mb-6">
          Tải xuống kết quả phân tích dưới các định dạng khác nhau
        </p>
        <div className="flex flex-wrap gap-3">
          <button
            onClick={() => handleExport('pdf')}
            disabled={exporting !== null}
            className="flex items-center gap-2 px-4 py-2.5 bg-red-600 hover:bg-red-700 text-white rounded-lg font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {exporting === 'pdf' ? (
              <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
            ) : (
              <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                <path d="M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M10.92,12.31C10.68,11.54 10.15,9.08 11.55,9.04C12.95,9 12.03,12.16 12.03,12.16C12.42,13.65 14.05,14.72 14.05,14.72C14.55,14.57 17.4,14.24 17,15.72C16.57,17.2 13.5,15.81 13.5,15.81C11.55,15.95 10.09,16.47 10.09,16.47C8.96,18.58 7.64,19.5 7.1,18.61C6.43,17.5 9.23,16.07 9.23,16.07C10.68,13.72 10.9,12.35 10.92,12.31Z" />
              </svg>
            )}
            Xuất PDF
          </button>
          <button
            onClick={() => handleExport('csv')}
            disabled={exporting !== null}
            className="flex items-center gap-2 px-4 py-2.5 bg-green-600 hover:bg-green-700 text-white rounded-lg font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {exporting === 'csv' ? (
              <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
            ) : (
              <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                <path d="M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M10,19L12,15H9V10H15V15L13,19H10Z" />
              </svg>
            )}
            Xuất CSV
          </button>
          <button
            onClick={() => handleExport('json')}
            disabled={exporting !== null}
            className="flex items-center gap-2 px-4 py-2.5 bg-blue-600 hover:bg-blue-700 text-white rounded-lg font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {exporting === 'json' ? (
              <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
            ) : (
              <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                <path d="M5,3H7V5H5V10A2,2 0 0,1 3,12A2,2 0 0,1 5,14V19H7V21H5C3.93,20.73 3,20.1 3,19V15A2,2 0 0,0 1,13H0V11H1A2,2 0 0,0 3,9V5A2,2 0 0,1 5,3M19,3A2,2 0 0,1 21,5V9A2,2 0 0,0 23,11H24V13H23A2,2 0 0,0 21,15V19A2,2 0 0,1 19,21H17V19H19V14A2,2 0 0,1 21,12A2,2 0 0,1 19,10V5H17V3H19M12,15A1,1 0 0,1 13,16A1,1 0 0,1 12,17A1,1 0 0,1 11,16A1,1 0 0,1 12,15M8,15A1,1 0 0,1 9,16A1,1 0 0,1 8,17A1,1 0 0,1 7,16A1,1 0 0,1 8,15M16,15A1,1 0 0,1 17,16A1,1 0 0,1 16,17A1,1 0 0,1 15,16A1,1 0 0,1 16,15Z" />
              </svg>
            )}
            Xuất JSON
          </button>
        </div>
        <div className="mt-4 pt-4 border-t border-slate-200 dark:border-slate-800">
          <Link
            to={isDoctor ? "/doctor/exports" : "/exports"}
            className="text-sm text-blue-600 dark:text-blue-400 hover:underline flex items-center gap-1"
          >
            Xem lịch sử xuất báo cáo
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
            </svg>
          </Link>
        </div>
      </div>
      )}

      {/* FR-15: Validate/Correct Modal */}
      {showValidateModal && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-xl max-w-lg w-full">
            <div className="p-6 border-b border-slate-200 dark:border-slate-800">
              <div className="flex items-center justify-between">
                <h2 className="text-xl font-bold text-slate-900 dark:text-white flex items-center gap-2">
                  <svg className="w-6 h-6 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  Xác nhận / Sửa kết quả AI
                </h2>
                <button onClick={() => setShowValidateModal(false)} className="text-slate-400 hover:text-slate-600">
                  <svg className="w-6 h-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            </div>
            <div className="p-6 space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">Trạng thái xác nhận</label>
                <div className="flex gap-4">
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="radio"
                      checked={validationStatus === 'Validated'}
                      onChange={() => setValidationStatus('Validated')}
                      className="w-4 h-4 text-blue-600"
                    />
                    <span className="text-slate-700 dark:text-slate-300">Kết quả chính xác</span>
                  </label>
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="radio"
                      checked={validationStatus === 'Corrected'}
                      onChange={() => setValidationStatus('Corrected')}
                      className="w-4 h-4 text-orange-600"
                    />
                    <span className="text-slate-700 dark:text-slate-300">Cần sửa đổi</span>
                  </label>
                </div>
              </div>

              {validationStatus === 'Corrected' && (
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">Mức rủi ro chính xác</label>
                  <select
                    value={correctedRiskLevel}
                    onChange={(e) => setCorrectedRiskLevel(e.target.value)}
                    className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100"
                  >
                    <option value="Low">Thấp</option>
                    <option value="Medium">Trung bình</option>
                    <option value="High">Cao</option>
                    <option value="Critical">Nghiêm trọng</option>
                  </select>
                </div>
              )}

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">Ghi chú của bác sĩ</label>
                <textarea
                  value={validationNotes}
                  onChange={(e) => setValidationNotes(e.target.value)}
                  rows={4}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100 resize-none"
                  placeholder="Nhập ghi chú về kết quả phân tích..."
                />
              </div>
            </div>
            <div className="p-6 border-t border-slate-200 dark:border-slate-800 flex justify-end gap-3">
              <button
                onClick={() => setShowValidateModal(false)}
                className="px-4 py-2 text-slate-700 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-lg transition-colors"
              >
                Hủy
              </button>
              <button
                onClick={handleValidateFindings}
                disabled={validating}
                className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50 flex items-center gap-2"
              >
                {validating && <div className="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></div>}
                {validationStatus === 'Validated' ? 'Xác nhận' : 'Lưu sửa đổi'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* FR-19: AI Feedback Modal */}
      {showFeedbackModal && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-xl max-w-lg w-full">
            <div className="p-6 border-b border-slate-200 dark:border-slate-800">
              <div className="flex items-center justify-between">
                <h2 className="text-xl font-bold text-slate-900 dark:text-white flex items-center gap-2">
                  <svg className="w-6 h-6 text-purple-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z" />
                  </svg>
                  Phản hồi để cải thiện AI
                </h2>
                <button onClick={() => setShowFeedbackModal(false)} className="text-slate-400 hover:text-slate-600">
                  <svg className="w-6 h-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            </div>
            <div className="p-6 space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">Loại phản hồi</label>
                <select
                  value={feedbackType}
                  onChange={(e) => setFeedbackType(e.target.value as any)}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100"
                >
                  <option value="Correct">Chính xác</option>
                  <option value="PartiallyCorrect">Đúng một phần</option>
                  <option value="Incorrect">Không chính xác</option>
                  <option value="NeedsReview">Cần xem xét thêm</option>
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Đánh giá chất lượng (1-5)
                </label>
                <div className="flex gap-2">
                  {[1, 2, 3, 4, 5].map((star) => (
                    <button
                      key={star}
                      onClick={() => setFeedbackRating(star)}
                      className={`w-10 h-10 rounded-lg border-2 transition-all ${
                        feedbackRating >= star
                          ? 'bg-yellow-400 border-yellow-400 text-white'
                          : 'border-slate-300 dark:border-slate-600 text-slate-400'
                      }`}
                    >
                      <svg className="w-5 h-5 mx-auto" fill="currentColor" viewBox="0 0 24 24">
                        <path d="M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z" />
                      </svg>
                    </button>
                  ))}
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Chi tiết phản hồi <span className="text-red-500">*</span>
                </label>
                <textarea
                  value={feedbackNotes}
                  onChange={(e) => setFeedbackNotes(e.target.value)}
                  rows={4}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100 resize-none"
                  placeholder="Mô tả chi tiết về kết quả AI (ví dụ: AI phát hiện đúng nhưng mức độ rủi ro cần điều chỉnh)..."
                />
              </div>

              <div className="p-4 bg-blue-50 dark:bg-blue-900/20 rounded-lg text-sm text-slate-600 dark:text-slate-400">
                <strong>Lưu ý:</strong> Phản hồi của bạn sẽ giúp cải thiện độ chính xác của mô hình AI trong các phân tích tiếp theo.
              </div>
            </div>
            <div className="p-6 border-t border-slate-200 dark:border-slate-800 flex justify-end gap-3">
              <button
                onClick={() => setShowFeedbackModal(false)}
                className="px-4 py-2 text-slate-700 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-lg transition-colors"
              >
                Hủy
              </button>
              <button
                onClick={handleSubmitFeedback}
                disabled={submittingFeedback || !feedbackNotes.trim()}
                className="px-4 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 transition-colors disabled:opacity-50 flex items-center gap-2"
              >
                {submittingFeedback && <div className="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></div>}
                Gửi phản hồi
              </button>
            </div>
          </div>
        </div>
      )}
    </main>
  );
};

export default AnalysisResultDisplay;

