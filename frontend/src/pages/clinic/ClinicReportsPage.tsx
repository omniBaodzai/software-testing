import { useState, useEffect, useMemo } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import ClinicHeader from "../../components/clinic/ClinicHeader";
import clinicImageService, {
  ClinicAnalysisReportItem,
} from "../../services/clinicImageService";
import clinicAuthService from "../../services/clinicAuthService";
import toast from "react-hot-toast";

type ViewMode = "timeline" | "list";
type FilterStatus = "all" | "Completed" | "Processing" | "Failed";

const ClinicReportsPage = () => {
  const navigate = useNavigate();
  const location = useLocation();
  const [reports, setReports] = useState<ClinicAnalysisReportItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [viewMode, setViewMode] = useState<ViewMode>("timeline");
  const [filterRisk, setFilterRisk] = useState<string>("all");
  const [filterStatus, setFilterStatus] = useState<FilterStatus>("all");
  const [filterPatient, setFilterPatient] = useState<string>("");
  const [sortBy, setSortBy] = useState<string>("date-desc");
  const [dateRangeStart, setDateRangeStart] = useState<string>("");
  const [dateRangeEnd, setDateRangeEnd] = useState<string>("");
  const [searchQuery, setSearchQuery] = useState<string>("");

  useEffect(() => {
    (async () => {
      const ok = await clinicAuthService.ensureLoggedIn();
      if (!ok) {
        navigate("/login");
        return;
      }
      loadReports();
    })();
  }, [navigate, location.pathname]);

  const loadReports = async () => {
    try {
      setLoading(true);
      const data = await clinicImageService.getClinicAnalysisReports();
      setReports(data);
    } catch (error: any) {
      console.error("Error loading reports:", error);
      toast.error("Lỗi khi tải lịch sử phân tích");
    } finally {
      setLoading(false);
    }
  };

  const getRiskColor = (risk?: string) => {
    switch (risk) {
      case "Low":
        return "bg-emerald-100 dark:bg-emerald-900/30 text-emerald-700 dark:text-emerald-400 border-emerald-300 dark:border-emerald-700";
      case "Medium":
        return "bg-amber-100 dark:bg-amber-900/30 text-amber-700 dark:text-amber-400 border-amber-300 dark:border-amber-700";
      case "High":
        return "bg-orange-100 dark:bg-orange-900/30 text-orange-700 dark:text-orange-400 border-orange-300 dark:border-orange-700";
      case "Critical":
        return "bg-red-100 dark:bg-red-900/30 text-red-700 dark:text-red-400 border-red-300 dark:border-red-700";
      default:
        return "bg-slate-100 dark:bg-slate-800 text-slate-700 dark:text-slate-400 border-slate-300 dark:border-slate-700";
    }
  };

  const getRiskLabel = (risk?: string) => {
    switch (risk) {
      case "Low":
        return "Rủi ro thấp";
      case "Medium":
        return "Rủi ro trung bình";
      case "High":
        return "Rủi ro cao";
      case "Critical":
        return "Rủi ro nghiêm trọng";
      default:
        return "Chưa xác định";
    }
  };

  const getRiskIcon = (risk?: string) => {
    switch (risk) {
      case "Low":
        return (
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
          </svg>
        );
      case "Medium":
      case "High":
      case "Critical":
        return (
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
          </svg>
        );
      default:
        return null;
    }
  };

  const getStatusColor = (status?: string) => {
    switch (status) {
      case "Completed":
        return "bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-400";
      case "Processing":
        return "bg-blue-100 dark:bg-blue-900/30 text-blue-700 dark:text-blue-400";
      case "Failed":
        return "bg-red-100 dark:bg-red-900/30 text-red-700 dark:text-red-400";
      default:
        return "bg-slate-100 dark:bg-slate-800 text-slate-700 dark:text-slate-400";
    }
  };

  const getStatusLabel = (status?: string) => {
    switch (status) {
      case "Completed":
        return "Hoàn thành";
      case "Processing":
        return "Đang xử lý";
      case "Failed":
        return "Thất bại";
      default:
        return "Chưa xác định";
    }
  };

  const getRiskLevelLabel = (risk?: string) => {
    switch (risk) {
      case "Low":
        return "Thấp";
      case "Medium":
        return "Trung bình";
      case "High":
        return "Cao";
      case "Critical":
        return "Nghiêm trọng";
      default:
        return "Chưa xác định";
    }
  };

  const formatDate = (dateString?: string) => {
    if (!dateString) return "N/A";
    const date = new Date(dateString);
    return date.toLocaleString("vi-VN", {
      timeZone: "Asia/Ho_Chi_Minh",
      year: "numeric",
      month: "long",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  };

  const formatDateGroup = (dateString?: string) => {
    if (!dateString) return "Không xác định";
    const date = new Date(dateString);
    const today = new Date();
    const yesterday = new Date(today);
    yesterday.setDate(yesterday.getDate() - 1);

    const vnDate = new Date(date.toLocaleString("en-US", { timeZone: "Asia/Ho_Chi_Minh" }));
    const vnToday = new Date(today.toLocaleString("en-US", { timeZone: "Asia/Ho_Chi_Minh" }));
    const vnYesterday = new Date(yesterday.toLocaleString("en-US", { timeZone: "Asia/Ho_Chi_Minh" }));

    if (vnDate.toDateString() === vnToday.toDateString()) {
      return "Hôm nay";
    } else if (vnDate.toDateString() === vnYesterday.toDateString()) {
      return "Hôm qua";
    } else {
      return date.toLocaleDateString("vi-VN", {
        timeZone: "Asia/Ho_Chi_Minh",
        year: "numeric",
        month: "long",
        day: "numeric",
      });
    }
  };

  const filteredAndSortedReports = useMemo(() => {
    let filtered = [...reports];

    if (filterRisk !== "all") {
      filtered = filtered.filter((r) => r.overallRiskLevel === filterRisk);
    }

    if (filterStatus !== "all") {
      filtered = filtered.filter((r) => r.analysisStatus === filterStatus);
    }

    if (filterPatient.trim()) {
      const q = filterPatient.toLowerCase().trim();
      filtered = filtered.filter((r) => {
        const name = (r.patientName || "").toLowerCase();
        return name.includes(q);
      });
    }

    if (dateRangeStart) {
      const startDate = new Date(dateRangeStart);
      filtered = filtered.filter((r) => {
        if (!r.analysisStartedAt) return false;
        return new Date(r.analysisStartedAt) >= startDate;
      });
    }
    if (dateRangeEnd) {
      const endDate = new Date(dateRangeEnd);
      endDate.setHours(23, 59, 59, 999);
      filtered = filtered.filter((r) => {
        if (!r.analysisStartedAt) return false;
        return new Date(r.analysisStartedAt) <= endDate;
      });
    }

    if (searchQuery) {
      const query = searchQuery.toLowerCase();
      filtered = filtered.filter((r) => {
        const riskLabel = getRiskLabel(r.overallRiskLevel).toLowerCase();
        const dateStr = formatDate(r.analysisStartedAt).toLowerCase();
        const patientStr = (r.patientName || "").toLowerCase();
        return riskLabel.includes(query) || dateStr.includes(query) || patientStr.includes(query);
      });
    }

    filtered.sort((a, b) => {
      if (sortBy === "date-desc") {
        const dateA = a.analysisStartedAt ? new Date(a.analysisStartedAt).getTime() : 0;
        const dateB = b.analysisStartedAt ? new Date(b.analysisStartedAt).getTime() : 0;
        return dateB - dateA;
      } else if (sortBy === "date-asc") {
        const dateA = a.analysisStartedAt ? new Date(a.analysisStartedAt).getTime() : 0;
        const dateB = b.analysisStartedAt ? new Date(b.analysisStartedAt).getTime() : 0;
        return dateA - dateB;
      } else if (sortBy === "risk-desc") {
        const riskOrder = { Critical: 4, High: 3, Medium: 2, Low: 1 };
        const riskA = riskOrder[a.overallRiskLevel as keyof typeof riskOrder] || 0;
        const riskB = riskOrder[b.overallRiskLevel as keyof typeof riskOrder] || 0;
        return riskB - riskA;
      }
      return 0;
    });

    return filtered;
  }, [reports, filterRisk, filterStatus, filterPatient, dateRangeStart, dateRangeEnd, searchQuery, sortBy]);

  const groupedReports = useMemo(() => {
    const groups: Record<string, ClinicAnalysisReportItem[]> = {};

    filteredAndSortedReports.forEach((report) => {
      const dateKey = report.analysisStartedAt
        ? new Date(report.analysisStartedAt).toDateString()
        : "Unknown";

      if (!groups[dateKey]) {
        groups[dateKey] = [];
      }
      groups[dateKey].push(report);
    });

    return Object.entries(groups)
      .map(([date, items]) => ({ date, reports: items }))
      .sort((a, b) => {
        if (a.date === "Unknown") return 1;
        if (b.date === "Unknown") return -1;
        return new Date(b.date).getTime() - new Date(a.date).getTime();
      });
  }, [filteredAndSortedReports]);

  const clearFilters = () => {
    setFilterRisk("all");
    setFilterStatus("all");
    setFilterPatient("");
    setDateRangeStart("");
    setDateRangeEnd("");
    setSearchQuery("");
  };

  const hasActiveFilters =
    filterRisk !== "all" ||
    filterStatus !== "all" ||
    filterPatient.trim() ||
    dateRangeStart ||
    dateRangeEnd ||
    searchQuery;

  const handleViewDetail = (report: ClinicAnalysisReportItem) => {
    navigate(`/clinic/analysis/result/${report.id}`);
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-slate-50 dark:bg-slate-950 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-slate-600 dark:text-slate-400">Đang tải...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950">
      <ClinicHeader />

      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <div className="mb-8">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div>
              <h1 className="text-3xl font-bold text-slate-900 dark:text-white mb-2">
                Lịch Sử Phân Tích
              </h1>
              <p className="text-slate-600 dark:text-slate-400">
                Xem lại tất cả kết quả phân tích AI của phòng khám
              </p>
            </div>

            <div className="flex items-center gap-3">
              <button
                onClick={() => navigate("/clinic/reports/create")}
                className="px-4 py-2 text-sm font-medium text-blue-600 dark:text-blue-400 border border-blue-600 dark:border-blue-400 rounded-lg hover:bg-blue-50 dark:hover:bg-blue-900/20 transition-colors"
              >
                Tạo báo cáo tổng hợp
              </button>
              <div className="flex items-center gap-2 bg-white dark:bg-slate-900 rounded-lg p-1 border border-slate-200 dark:border-slate-800">
              <button
                onClick={() => setViewMode("timeline")}
                className={`px-4 py-2 rounded-md text-sm font-medium transition-colors ${
                  viewMode === "timeline"
                    ? "bg-blue-600 text-white"
                    : "text-slate-600 dark:text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800"
                }`}
              >
                <span className="flex items-center gap-2">
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  Dòng thời gian
                </span>
              </button>
              <button
                onClick={() => setViewMode("list")}
                className={`px-4 py-2 rounded-md text-sm font-medium transition-colors ${
                  viewMode === "list"
                    ? "bg-blue-600 text-white"
                    : "text-slate-600 dark:text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800"
                }`}
              >
                <span className="flex items-center gap-2">
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h16" />
                  </svg>
                  Danh sách
                </span>
              </button>
              </div>
            </div>
          </div>
        </div>

        <div className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-4 mb-6">
          <div className="flex flex-col gap-4">
            <div>
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                Tìm kiếm
              </label>
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Tìm kiếm theo rủi ro, ngày, bệnh nhân..."
                className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
              />
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-6 gap-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Bệnh nhân
                </label>
                <input
                  type="text"
                  value={filterPatient}
                  onChange={(e) => setFilterPatient(e.target.value)}
                  placeholder="Tên bệnh nhân..."
                  className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Mức độ rủi ro
                </label>
                <select
                  value={filterRisk}
                  onChange={(e) => setFilterRisk(e.target.value)}
                  className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                >
                  <option value="all">Tất cả</option>
                  <option value="Low">Rủi ro thấp</option>
                  <option value="Medium">Rủi ro trung bình</option>
                  <option value="High">Rủi ro cao</option>
                  <option value="Critical">Rủi ro nghiêm trọng</option>
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Trạng thái
                </label>
                <select
                  value={filterStatus}
                  onChange={(e) => setFilterStatus(e.target.value as FilterStatus)}
                  className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                >
                  <option value="all">Tất cả</option>
                  <option value="Completed">Hoàn thành</option>
                  <option value="Processing">Đang xử lý</option>
                  <option value="Failed">Thất bại</option>
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Từ ngày
                </label>
                <input
                  type="date"
                  value={dateRangeStart}
                  onChange={(e) => setDateRangeStart(e.target.value)}
                  className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Đến ngày
                </label>
                <input
                  type="date"
                  value={dateRangeEnd}
                  onChange={(e) => setDateRangeEnd(e.target.value)}
                  className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                  Sắp xếp
                </label>
                <select
                  value={sortBy}
                  onChange={(e) => setSortBy(e.target.value)}
                  className="w-full px-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                >
                  <option value="date-desc">Mới nhất</option>
                  <option value="date-asc">Cũ nhất</option>
                  <option value="risk-desc">Rủi ro cao nhất</option>
                </select>
              </div>
            </div>

            {hasActiveFilters && (
              <div className="flex justify-end">
                <button
                  onClick={clearFilters}
                  className="px-4 py-2 text-sm text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-white"
                >
                  Xóa bộ lọc
                </button>
              </div>
            )}
          </div>
        </div>

        <div className="mb-4 text-sm text-slate-600 dark:text-slate-400">
          Hiển thị {filteredAndSortedReports.length} / {reports.length} báo cáo
        </div>

        {filteredAndSortedReports.length === 0 ? (
          <div className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-12 text-center">
            <svg
              className="w-16 h-16 mx-auto text-slate-400 dark:text-slate-600 mb-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
              />
            </svg>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-2">
              Chưa có báo cáo nào
            </h3>
            <p className="text-slate-600 dark:text-slate-400 mb-6">
              {hasActiveFilters
                ? "Không tìm thấy báo cáo với bộ lọc đã chọn."
                : "Phòng khám chưa có kết quả phân tích nào. Hãy upload ảnh để bắt đầu."}
            </p>
            {hasActiveFilters ? (
              <button
                onClick={clearFilters}
                className="inline-flex items-center gap-2 px-6 py-3 bg-blue-600 text-white rounded-lg font-semibold hover:bg-blue-700 transition-colors"
              >
                Xóa bộ lọc
              </button>
            ) : (
              <button
                onClick={() => navigate("/clinic/upload")}
                className="inline-flex items-center gap-2 px-6 py-3 bg-blue-600 text-white rounded-lg font-semibold hover:bg-blue-700 transition-colors"
              >
                <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
                </svg>
                Upload ảnh
              </button>
            )}
          </div>
        ) : viewMode === "timeline" ? (
          <div className="relative">
            <div className="absolute left-8 top-0 bottom-0 w-0.5 bg-slate-200 dark:bg-slate-700"></div>

            <div className="space-y-8">
              {groupedReports.map((group) => (
                <div key={group.date} className="relative">
                  <div className="flex items-center gap-4 mb-4">
                    <div className="relative z-10 flex items-center justify-center w-16 h-16 rounded-full bg-blue-600 text-white font-bold text-sm shadow-lg">
                      {new Date(group.date).getDate()}
                    </div>
                    <div>
                      <h2 className="text-xl font-bold text-slate-900 dark:text-white">
                        {formatDateGroup(group.date)}
                      </h2>
                      <p className="text-sm text-slate-600 dark:text-slate-400">
                        {group.reports.length} {group.reports.length === 1 ? "phân tích" : "phân tích"}
                      </p>
                    </div>
                  </div>

                  <div className="ml-8 space-y-4">
                    {group.reports.map((report) => (
                      <div
                        key={report.id}
                        className="relative bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-6 hover:shadow-md transition-all cursor-pointer group"
                        onClick={() => handleViewDetail(report)}
                      >
                        <div
                          className={`absolute -left-12 top-6 w-4 h-4 rounded-full border-4 border-white dark:border-slate-900 ${getRiskColor(report.overallRiskLevel).split(" ")[0]}`}
                        ></div>

                        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                          <div className="flex items-start gap-4 flex-1">
                            <div
                              className={`h-12 w-12 rounded-lg flex items-center justify-center flex-shrink-0 border-2 ${getRiskColor(
                                report.overallRiskLevel
                              )}`}
                            >
                              {getRiskIcon(report.overallRiskLevel)}
                            </div>

                            <div className="flex-1 min-w-0">
                              <div className="flex items-center gap-3 mb-2 flex-wrap">
                                <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
                                  Phân tích võng mạc
                                  {report.patientName && (
                                    <span className="ml-2 text-sm font-normal text-slate-500 dark:text-slate-400">
                                      — {report.patientName}
                                    </span>
                                  )}
                                </h3>
                                <span
                                  className={`px-3 py-1 rounded-full text-xs font-semibold ${getRiskColor(
                                    report.overallRiskLevel
                                  )}`}
                                >
                                  {getRiskLabel(report.overallRiskLevel)}
                                </span>
                                <span
                                  className={`px-3 py-1 rounded-full text-xs font-semibold ${getStatusColor(
                                    report.analysisStatus
                                  )}`}
                                >
                                  {getStatusLabel(report.analysisStatus)}
                                </span>
                              </div>
                              <p className="text-sm text-slate-600 dark:text-slate-400 mb-2">
                                {formatDate(report.analysisStartedAt)}
                              </p>
                              <div className="flex flex-wrap gap-4 text-sm">
                                {report.riskScore !== undefined && (
                                  <span className="text-slate-600 dark:text-slate-400">
                                    Điểm tổng:{" "}
                                    <span className="font-semibold text-slate-900 dark:text-white">
                                      {report.riskScore}/100
                                    </span>
                                  </span>
                                )}
                                {report.hypertensionRisk && (
                                  <span className="text-slate-600 dark:text-slate-400">
                                    Tim mạch:{" "}
                                    <span className="font-semibold text-slate-900 dark:text-white">
                                      {getRiskLevelLabel(report.hypertensionRisk)}
                                    </span>
                                  </span>
                                )}
                                {report.diabetesRisk && (
                                  <span className="text-slate-600 dark:text-slate-400">
                                    Tiểu đường:{" "}
                                    <span className="font-semibold text-slate-900 dark:text-white">
                                      {getRiskLevelLabel(report.diabetesRisk)}
                                    </span>
                                  </span>
                                )}
                              </div>
                            </div>
                          </div>

                          <div className="flex items-center gap-2">
                            <button
                              onClick={(e) => {
                                e.stopPropagation();
                                handleViewDetail(report);
                              }}
                              className="px-4 py-2 bg-blue-600 text-white rounded-lg font-medium hover:bg-blue-700 transition-colors"
                            >
                              Xem chi tiết
                            </button>
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            {filteredAndSortedReports.map((report) => (
              <div
                key={report.id}
                className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-6 hover:shadow-md transition-shadow cursor-pointer"
                onClick={() => handleViewDetail(report)}
              >
                <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                  <div className="flex items-start gap-4 flex-1">
                    <div
                      className={`h-12 w-12 rounded-lg flex items-center justify-center flex-shrink-0 border-2 ${getRiskColor(
                        report.overallRiskLevel
                      )}`}
                    >
                      {getRiskIcon(report.overallRiskLevel)}
                    </div>

                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-3 mb-2 flex-wrap">
                        <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
                          Phân tích võng mạc
                          {report.patientName && (
                            <span className="ml-2 text-sm font-normal text-slate-500 dark:text-slate-400">
                              — {report.patientName}
                            </span>
                          )}
                        </h3>
                        <span
                          className={`px-3 py-1 rounded-full text-xs font-semibold ${getRiskColor(
                            report.overallRiskLevel
                          )}`}
                        >
                          {getRiskLabel(report.overallRiskLevel)}
                        </span>
                        <span
                          className={`px-3 py-1 rounded-full text-xs font-semibold ${getStatusColor(
                            report.analysisStatus
                          )}`}
                        >
                          {getStatusLabel(report.analysisStatus)}
                        </span>
                      </div>
                      <p className="text-sm text-slate-600 dark:text-slate-400 mb-2">
                        {formatDate(report.analysisStartedAt)}
                      </p>
                      <div className="flex flex-wrap gap-4 text-sm">
                        {report.riskScore !== undefined && (
                          <span className="text-slate-600 dark:text-slate-400">
                            Điểm tổng:{" "}
                            <span className="font-semibold text-slate-900 dark:text-white">
                              {report.riskScore}/100
                            </span>
                          </span>
                        )}
                        {report.hypertensionRisk && (
                          <span className="text-slate-600 dark:text-slate-400">
                            Tim mạch:{" "}
                            <span className="font-semibold text-slate-900 dark:text-white">
                              {getRiskLevelLabel(report.hypertensionRisk)}
                            </span>
                          </span>
                        )}
                        {report.diabetesRisk && (
                          <span className="text-slate-600 dark:text-slate-400">
                            Tiểu đường:{" "}
                            <span className="font-semibold text-slate-900 dark:text-white">
                              {getRiskLevelLabel(report.diabetesRisk)}
                            </span>
                          </span>
                        )}
                      </div>
                    </div>
                  </div>

                  <div className="flex items-center gap-2">
                    <button
                      onClick={(e) => {
                        e.stopPropagation();
                        handleViewDetail(report);
                      }}
                      className="px-4 py-2 bg-blue-600 text-white rounded-lg font-medium hover:bg-blue-700 transition-colors"
                    >
                      Xem chi tiết
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}

        {reports.length > 0 && (
          <div className="mt-8 grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-4">
              <div className="text-sm text-slate-600 dark:text-slate-400 mb-1">Tổng số báo cáo</div>
              <div className="text-2xl font-bold text-slate-900 dark:text-white">{reports.length}</div>
            </div>
            <div className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-4">
              <div className="text-sm text-slate-600 dark:text-slate-400 mb-1">Rủi ro thấp</div>
              <div className="text-2xl font-bold text-emerald-600 dark:text-emerald-400">
                {reports.filter((r) => r.overallRiskLevel === "Low").length}
              </div>
            </div>
            <div className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-4">
              <div className="text-sm text-slate-600 dark:text-slate-400 mb-1">Rủi ro trung bình</div>
              <div className="text-2xl font-bold text-amber-600 dark:text-amber-400">
                {reports.filter((r) => r.overallRiskLevel === "Medium").length}
              </div>
            </div>
            <div className="bg-white dark:bg-slate-900 rounded-lg shadow-sm border border-slate-200 dark:border-slate-800 p-4">
              <div className="text-sm text-slate-600 dark:text-slate-400 mb-1">Rủi ro cao</div>
              <div className="text-2xl font-bold text-red-600 dark:text-red-400">
                {reports.filter((r) => r.overallRiskLevel === "High" || r.overallRiskLevel === "Critical").length}
              </div>
            </div>
          </div>
        )}
      </main>
    </div>
  );
};

export default ClinicReportsPage;
