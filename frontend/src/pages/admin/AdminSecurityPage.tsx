import { useState, useEffect } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import toast from "react-hot-toast";
import { useAdminAuthStore } from "../../store/adminAuthStore";
import AdminHeader from "../../components/admin/AdminHeader";
import StatCard from "../../components/admin/StatCard";
import TabButton from "../../components/admin/TabButton";
import { Field, ReadOnlyField } from "../../components/admin/FormField";
import auditApi, { AuditLog, AuditLogFilter } from "../../services/auditApi";
import complianceApi, {
  ComplianceReport,
  PrivacySettings,
  UpdatePrivacySettingsDto,
} from "../../services/complianceApi";

type Tab = "audit-logs" | "compliance";

export default function AdminSecurityPage() {
  const navigate = useNavigate();
  const { isAdminAuthenticated, logoutAdmin } = useAdminAuthStore();
  const [searchParams, setSearchParams] = useSearchParams();
  const [activeTab, setActiveTab] = useState<Tab>(
    (searchParams.get("tab") as Tab) || "audit-logs"
  );

  // Common state
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);

  // Audit Logs state
  const [logs, setLogs] = useState<AuditLog[]>([]);
  const [selectedLog, setSelectedLog] = useState<AuditLog | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(50);
  const [total, setTotal] = useState(0);
  const [totalPages, setTotalPages] = useState(0);
  const [filters, setFilters] = useState<AuditLogFilter>({
    page: 1,
    pageSize: 50,
  });

  // Compliance state
  const [report, setReport] = useState<ComplianceReport | null>(null);
  const [privacySettings, setPrivacySettings] =
    useState<PrivacySettings | null>(null);
  const [complianceSubTab, setComplianceSubTab] = useState<
    "dashboard" | "privacy"
  >("dashboard");

  useEffect(() => {
    if (!isAdminAuthenticated) {
      navigate("/admin/login");
      return;
    }
    setSearchParams({ tab: activeTab });
    if (activeTab === "audit-logs") {
      loadAuditLogs();
    } else {
      loadComplianceData();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, page, filters, complianceSubTab]);

  const loadAuditLogs = async () => {
    if (!isAdminAuthenticated) return;
    setLoading(true);
    try {
      const response = await auditApi.getAll({ ...filters, page, pageSize });
      setLogs(response.data);
      setTotal(response.total);
      setTotalPages(response.totalPages);
    } catch (e: any) {
      if (e?.response?.status === 401) {
        toast.error("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
        logoutAdmin();
        window.location.href = "/admin/login";
        return;
      }
      toast.error(
        e?.response?.data?.message || e?.message || "Không tải được dữ liệu"
      );
    } finally {
      setLoading(false);
    }
  };

  const loadComplianceData = async () => {
    if (!isAdminAuthenticated) return;
    setLoading(true);
    try {
      if (complianceSubTab === "dashboard") {
        const data = await complianceApi.getReport();
        setReport(data);
      } else {
        const data = await complianceApi.getPrivacySettings();
        setPrivacySettings(data);
      }
    } catch (e: any) {
      if (e?.response?.status === 401) {
        toast.error("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
        logoutAdmin();
        window.location.href = "/admin/login";
        return;
      }
      toast.error(
        e?.response?.data?.message || e?.message || "Không tải được dữ liệu"
      );
    } finally {
      setLoading(false);
    }
  };

  const handleExport = async (format: "json" | "csv") => {
    try {
      const data = await auditApi.export(filters, format);
      if (format === "csv") {
        const blob = data as Blob;
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = `audit-logs-${new Date().toISOString().split("T")[0]}.csv`;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(url);
        document.body.removeChild(a);
        toast.success("Đã xuất file CSV");
      } else {
        const json = JSON.stringify(data, null, 2);
        const blob = new Blob([json], { type: "application/json" });
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = `audit-logs-${
          new Date().toISOString().split("T")[0]
        }.json`;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(url);
        document.body.removeChild(a);
        toast.success("Đã xuất file JSON");
      }
    } catch (e: any) {
      toast.error(
        e?.response?.data?.message || e?.message || "Không xuất được file"
      );
    }
  };

  const savePrivacySettings = async () => {
    if (!privacySettings) return;
    setSaving(true);
    try {
      const dto: UpdatePrivacySettingsDto = {
        enableAuditLogging: privacySettings.enableAuditLogging,
        auditLogRetentionDays: privacySettings.auditLogRetentionDays,
        anonymizeOldLogs: privacySettings.anonymizeOldLogs,
        requireConsentForDataSharing:
          privacySettings.requireConsentForDataSharing,
        enableGdprCompliance: privacySettings.enableGdprCompliance,
        dataRetentionDays: privacySettings.dataRetentionDays,
        allowDataExport: privacySettings.allowDataExport,
        requireTwoFactorForSensitiveActions:
          privacySettings.requireTwoFactorForSensitiveActions,
      };
      await complianceApi.updatePrivacySettings(dto);
      toast.success("Đã lưu cài đặt quyền riêng tư");
      await loadComplianceData();
    } catch (e: any) {
      toast.error(
        e?.response?.data?.message ||
          e?.message ||
          "Không lưu được cài đặt quyền riêng tư"
      );
    } finally {
      setSaving(false);
    }
  };

  const formatJson = (jsonStr?: string) => {
    if (!jsonStr) return "N/A";
    try {
      const obj = JSON.parse(jsonStr);
      return JSON.stringify(obj, null, 2);
    } catch {
      return jsonStr;
    }
  };

  const createSampleLogs = async () => {
    if (!isAdminAuthenticated) return;
    setLoading(true);
    try {
      await complianceApi.createSampleLogs();
      toast.success("Đã tạo dữ liệu mẫu");
      await loadAuditLogs();
    } catch (e: any) {
      toast.error(
        e?.response?.data?.message || e?.message || "Không tạo được dữ liệu mẫu"
      );
    } finally {
      setLoading(false);
    }
  };

  if (!isAdminAuthenticated) {
    return null;
  }

  return (
    <div className="bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50 font-sans antialiased min-h-screen flex flex-col transition-colors duration-200">
      <AdminHeader />

      <main className="flex-1 max-w-7xl mx-auto w-full px-4 sm:px-6 lg:px-8 py-8">
        <div className="mb-8">
          <h1 className="text-3xl font-bold text-slate-900 dark:text-white mb-2">
            Bảo mật & Tuân thủ
          </h1>
          <p className="text-slate-600 dark:text-slate-400">
            Quản lý nhật ký kiểm toán và thiết lập tuân thủ
          </p>
        </div>

        {/* Main Tabs */}
        <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 mb-6">
          <div className="border-b border-slate-200 dark:border-slate-800">
            <nav className="flex -mb-px">
              <TabButton
                active={activeTab === "audit-logs"}
                onClick={() => setActiveTab("audit-logs")}
                icon={
                  <svg
                    className="w-5 h-5"
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
                }
              >
                Nhật ký kiểm toán
              </TabButton>
              <TabButton
                active={activeTab === "compliance"}
                onClick={() => setActiveTab("compliance")}
                icon={
                  <svg
                    className="w-5 h-5"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z"
                    />
                  </svg>
                }
              >
                Tuân thủ
              </TabButton>
            </nav>
          </div>

          <div className="p-6">
            {/* Content based on tab */}
            {activeTab === "audit-logs" && (
              <AuditLogsContent
                logs={logs}
                loading={loading}
                selected={selectedLog}
                onSelect={setSelectedLog}
                page={page}
                totalPages={totalPages}
                total={total}
                onPageChange={setPage}
                filters={filters}
                onFiltersChange={setFilters}
                onExport={handleExport}
                onReload={loadAuditLogs}
                onCreateSampleLogs={createSampleLogs}
                formatJson={formatJson}
              />
            )}
            {activeTab === "compliance" && (
              <ComplianceContent
                report={report}
                privacySettings={privacySettings}
                loading={loading}
                saving={saving}
                subTab={complianceSubTab}
                onSubTabChange={setComplianceSubTab}
                onPrivacySettingsChange={setPrivacySettings}
                onSave={savePrivacySettings}
                onReload={loadComplianceData}
              />
            )}
          </div>
        </div>
      </main>
    </div>
  );
}

// Các giá trị lọc cố định để chọn nhanh (khớp backend)
const ACTION_TYPE_OPTIONS = [
  { value: "", label: "Tất cả" },
  { value: "CreatePackage", label: "Tạo gói dịch vụ" },
  { value: "UpdatePackage", label: "Cập nhật gói" },
  { value: "SetPackageStatus", label: "Đổi trạng thái gói" },
  { value: "DeletePackage", label: "Xóa gói" },
  { value: "CreateAIConfig", label: "Tạo cấu hình AI" },
  { value: "UpdateAIConfig", label: "Cập nhật cấu hình AI" },
  { value: "SetAIConfigStatus", label: "Đổi trạng thái cấu hình AI" },
  { value: "DeleteAIConfig", label: "Xóa cấu hình AI" },
  { value: "CreateNotificationTemplate", label: "Tạo mẫu thông báo" },
  { value: "UpdateNotificationTemplate", label: "Cập nhật mẫu thông báo" },
  { value: "SetNotificationTemplateStatus", label: "Đổi trạng thái mẫu" },
  { value: "DeleteNotificationTemplate", label: "Xóa mẫu thông báo" },
  { value: "UpdateUser", label: "Cập nhật người dùng" },
  { value: "SetUserStatus", label: "Đổi trạng thái người dùng" },
  { value: "UpdateDoctor", label: "Cập nhật bác sĩ" },
  { value: "SetDoctorStatus", label: "Đổi trạng thái bác sĩ" },
  { value: "ApproveClinic", label: "Phê duyệt phòng khám" },
  { value: "RejectClinic", label: "Từ chối phòng khám" },
  { value: "SuspendClinic", label: "Tạm ngưng phòng khám" },
  { value: "ActivateClinic", label: "Kích hoạt phòng khám" },
  { value: "UpdateClinic", label: "Cập nhật phòng khám" },
];
const RESOURCE_TYPE_OPTIONS = [
  { value: "", label: "Tất cả" },
  { value: "ServicePackage", label: "Gói dịch vụ" },
  { value: "AIConfiguration", label: "Cấu hình AI" },
  { value: "NotificationTemplate", label: "Mẫu thông báo" },
  { value: "User", label: "Người dùng" },
  { value: "Doctor", label: "Bác sĩ" },
  { value: "Clinic", label: "Phòng khám" },
];

function formatDateForInput(d: Date) {
  return d.toISOString().slice(0, 10);
}

// Sub-components
function AuditLogsContent({
  logs,
  loading,
  selected,
  onSelect,
  page,
  totalPages,
  total,
  onPageChange,
  filters,
  onFiltersChange,
  onExport,
  onReload,
  onCreateSampleLogs,
  formatJson,
}: any) {
  const setPresetDate = (preset: "today" | "7days" | "30days") => {
    const end = new Date();
    const start = new Date();
    if (preset === "today") {
      start.setHours(0, 0, 0, 0);
    } else if (preset === "7days") {
      start.setDate(start.getDate() - 7);
      start.setHours(0, 0, 0, 0);
    } else {
      start.setDate(start.getDate() - 30);
      start.setHours(0, 0, 0, 0);
    }
    onFiltersChange({
      ...filters,
      startDate: formatDateForInput(start),
      endDate: formatDateForInput(end),
      page: 1,
    });
    onPageChange(1);
  };

  const clearFilters = () => {
    onFiltersChange({
      page: 1,
      pageSize: 50,
      actionType: undefined,
      resourceType: undefined,
      userId: undefined,
      adminId: undefined,
      startDate: undefined,
      endDate: undefined,
      ipAddress: undefined,
    });
    onPageChange(1);
  };

  return (
    <>
      {/* Filters */}
      <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-6 mb-6">
        <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">
          Bộ lọc
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
              Loại hành động
            </label>
            <select
              className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100 focus:ring-2 focus:ring-blue-500"
              value={filters.actionType || ""}
              onChange={(e) =>
                onFiltersChange({
                  ...filters,
                  actionType: e.target.value || undefined,
                  page: 1,
                })
              }
            >
              {ACTION_TYPE_OPTIONS.map((o) => (
                <option key={o.value || "all"} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
              Loại tài nguyên
            </label>
            <select
              className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100 focus:ring-2 focus:ring-blue-500"
              value={filters.resourceType || ""}
              onChange={(e) =>
                onFiltersChange({
                  ...filters,
                  resourceType: e.target.value || undefined,
                  page: 1,
                })
              }
            >
              {RESOURCE_TYPE_OPTIONS.map((o) => (
                <option key={o.value || "all"} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
              Khoảng thời gian (nhanh)
            </label>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => setPresetDate("today")}
                className="px-3 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 text-sm"
              >
                Hôm nay
              </button>
              <button
                type="button"
                onClick={() => setPresetDate("7days")}
                className="px-3 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 text-sm"
              >
                7 ngày qua
              </button>
              <button
                type="button"
                onClick={() => setPresetDate("30days")}
                className="px-3 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 text-sm"
              >
                30 ngày qua
              </button>
            </div>
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
              Từ ngày
            </label>
            <input
              type="date"
              className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100"
              value={filters.startDate || ""}
              onChange={(e) =>
                onFiltersChange({
                  ...filters,
                  startDate: e.target.value || undefined,
                  page: 1,
                })
              }
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
              Đến ngày
            </label>
            <input
              type="date"
              className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-slate-100"
              value={filters.endDate || ""}
              onChange={(e) =>
                onFiltersChange({
                  ...filters,
                  endDate: e.target.value || undefined,
                  page: 1,
                })
              }
            />
          </div>
          <Field
            label="ID Người dùng"
            value={filters.userId || ""}
            onChange={(v) =>
              onFiltersChange({ ...filters, userId: v || undefined, page: 1 })
            }
          />
          <Field
            label="ID Quản trị viên"
            value={filters.adminId || ""}
            onChange={(v) =>
              onFiltersChange({ ...filters, adminId: v || undefined, page: 1 })
            }
          />
          <Field
            label="Địa chỉ IP"
            value={filters.ipAddress || ""}
            onChange={(v) =>
              onFiltersChange({
                ...filters,
                ipAddress: v || undefined,
                page: 1,
              })
            }
            placeholder="VD: 192.168.1.1"
          />
          <div className="flex items-end gap-2">
            <button
              type="button"
              onClick={clearFilters}
              className="px-4 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 transition-colors font-medium"
            >
              Xóa bộ lọc
            </button>
          </div>
        </div>
      </div>

      {/* Actions */}
      <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-4 mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="text-sm text-slate-600 dark:text-slate-400">
          Tổng:{" "}
          <span className="font-semibold text-slate-900 dark:text-white">
            {total.toLocaleString("vi-VN")}
          </span>{" "}
          bản ghi
        </div>
        <div className="flex flex-wrap gap-2">
          {total === 0 && onCreateSampleLogs && (
            <button
              type="button"
              onClick={onCreateSampleLogs}
              disabled={loading}
              className="px-4 py-2 rounded-lg bg-emerald-600 text-white hover:bg-emerald-700 disabled:opacity-60 transition-colors text-sm font-medium"
            >
              + Tạo dữ liệu mẫu
            </button>
          )}
          <button
            type="button"
            onClick={() => onExport("csv")}
            className="px-4 py-2 rounded-lg bg-green-600 text-white hover:bg-green-700 transition-colors text-sm font-medium"
          >
            Xuất CSV
          </button>
          <button
            type="button"
            onClick={() => onExport("json")}
            className="px-4 py-2 rounded-lg bg-blue-600 text-white hover:bg-blue-700 transition-colors text-sm font-medium"
          >
            Xuất JSON
          </button>
          <button
            type="button"
            onClick={onReload}
            disabled={loading}
            className="px-4 py-2 rounded-lg bg-slate-600 text-white hover:bg-slate-700 disabled:opacity-60 transition-colors text-sm font-medium"
          >
            {loading ? "Đang tải..." : "Tải lại"}
          </button>
        </div>
      </div>

      {/* Table */}
      <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 overflow-hidden mb-6">
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead className="bg-slate-50 dark:bg-slate-800/50">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 dark:text-slate-400 uppercase">
                  Thời gian
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 dark:text-slate-400 uppercase">
                  Người thực hiện
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 dark:text-slate-400 uppercase">
                  Hành động
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 dark:text-slate-400 uppercase">
                  Tài nguyên
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 dark:text-slate-400 uppercase">
                  Địa chỉ IP
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 dark:text-slate-400 uppercase">
                  Thao tác
                </th>
              </tr>
            </thead>
            <tbody className="bg-white dark:bg-slate-900 divide-y divide-slate-200 dark:divide-slate-800">
              {loading && logs.length === 0 ? (
                <tr>
                  <td
                    className="px-6 py-8 text-center text-slate-500 dark:text-slate-400"
                    colSpan={6}
                  >
                    Đang tải dữ liệu...
                  </td>
                </tr>
              ) : logs.length === 0 ? (
                <tr>
                  <td
                    className="px-6 py-8 text-center text-slate-500 dark:text-slate-400"
                    colSpan={6}
                  >
                    <p className="mb-3">Không có nhật ký kiểm toán nào.</p>
                    <p className="text-sm mb-4">
                      Thực hiện các thao tác trong hệ thống (sửa user, gói, cấu
                      hình…) hoặc tạo dữ liệu mẫu để xem log.
                    </p>
                    {onCreateSampleLogs && (
                      <button
                        type="button"
                        onClick={onCreateSampleLogs}
                        disabled={loading}
                        className="px-4 py-2 rounded-lg bg-emerald-600 text-white hover:bg-emerald-700 disabled:opacity-60 text-sm font-medium"
                      >
                        + Tạo dữ liệu mẫu
                      </button>
                    )}
                  </td>
                </tr>
              ) : (
                logs.map((log: AuditLog) => (
                  <tr
                    key={log.id}
                    className={`hover:bg-slate-50 dark:hover:bg-slate-800/50 ${
                      selected?.id === log.id
                        ? "bg-blue-50 dark:bg-blue-900/20"
                        : ""
                    }`}
                  >
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-slate-900 dark:text-slate-100">
                      {log.createdDate
                        ? new Date(log.createdDate).toLocaleString("vi-VN", {
                            timeZone: "Asia/Ho_Chi_Minh",
                          })
                        : "N/A"}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-slate-900 dark:text-slate-100">
                      {log.adminId
                        ? `Admin: ${log.adminId.substring(0, 8)}...`
                        : log.userId
                        ? `User: ${log.userId.substring(0, 8)}...`
                        : log.doctorId
                        ? `Doctor: ${log.doctorId.substring(0, 8)}...`
                        : "System"}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap">
                      <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400">
                        {log.actionType}
                      </span>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-slate-900 dark:text-slate-100">
                      {log.resourceType}
                      {log.resourceId && (
                        <span className="text-slate-500 dark:text-slate-400 ml-1">
                          ({log.resourceId.substring(0, 8)}...)
                        </span>
                      )}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-slate-500 dark:text-slate-400 font-mono">
                      {log.ipAddress || "N/A"}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium">
                      <button
                        onClick={() => onSelect(log)}
                        className="text-blue-600 hover:text-blue-900 dark:text-blue-400"
                      >
                        Xem chi tiết
                      </button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        {/* Pagination */}
        {totalPages > 1 && (
          <div className="px-6 py-4 border-t border-slate-200 dark:border-slate-800 flex items-center justify-between">
            <div className="text-sm text-slate-600 dark:text-slate-400">
              Trang {page} / {totalPages}
            </div>
            <div className="flex gap-2">
              <button
                onClick={() => onPageChange(Math.max(1, page - 1))}
                disabled={page === 1}
                className="px-4 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Trước
              </button>
              <button
                onClick={() => onPageChange(Math.min(totalPages, page + 1))}
                disabled={page === totalPages}
                className="px-4 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Sau
              </button>
            </div>
          </div>
        )}
      </div>

      {/* Detail Modal */}
      {selected && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white dark:bg-slate-900 rounded-lg p-6 w-full max-w-4xl max-h-[90vh] overflow-y-auto">
            <div className="flex items-center justify-between mb-6">
              <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
                Chi tiết nhật ký kiểm toán
              </h3>
              <button
                onClick={() => onSelect(null)}
                className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-300"
              >
                ✕
              </button>
            </div>
            <div className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <ReadOnlyField label="ID" value={selected.id} />
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                    Thời gian
                  </label>
                  <p className="text-sm text-slate-900 dark:text-slate-100">
                    {selected.createdDate
                      ? new Date(selected.createdDate).toLocaleString("vi-VN", {
                          timeZone: "Asia/Ho_Chi_Minh",
                        })
                      : "N/A"}
                  </p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                    Loại hành động
                  </label>
                  <p className="text-sm text-slate-900 dark:text-slate-100">
                    {selected.actionType}
                  </p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                    Loại tài nguyên
                  </label>
                  <p className="text-sm text-slate-900 dark:text-slate-100">
                    {selected.resourceType}
                  </p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                    ID tài nguyên
                  </label>
                  <p className="text-sm text-slate-900 dark:text-slate-100 font-mono">
                    {selected.resourceId || "N/A"}
                  </p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                    Địa chỉ IP
                  </label>
                  <p className="text-sm text-slate-900 dark:text-slate-100 font-mono">
                    {selected.ipAddress || "N/A"}
                  </p>
                </div>
              </div>
              {selected.oldValues && (
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                    Giá trị cũ
                  </label>
                  <pre className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-slate-50 dark:bg-slate-800 text-slate-900 dark:text-slate-100 text-xs font-mono overflow-x-auto">
                    {formatJson(selected.oldValues)}
                  </pre>
                </div>
              )}
              {selected.newValues && (
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">
                    Giá trị mới
                  </label>
                  <pre className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-slate-50 dark:bg-slate-800 text-slate-900 dark:text-slate-100 text-xs font-mono overflow-x-auto">
                    {formatJson(selected.newValues)}
                  </pre>
                </div>
              )}
              {selected.userAgent && (
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                    User Agent
                  </label>
                  <p className="text-sm text-slate-900 dark:text-slate-100 break-all">
                    {selected.userAgent}
                  </p>
                </div>
              )}
              <div className="flex gap-2 pt-4 border-t">
                <button
                  onClick={() => onSelect(null)}
                  className="flex-1 px-4 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 transition-colors"
                >
                  Đóng
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

function ComplianceContent({
  report,
  privacySettings,
  loading,
  saving,
  subTab,
  onSubTabChange,
  onPrivacySettingsChange,
  onSave,
  onReload,
}: any) {
  return (
    <>
      {/* Sub-tabs */}
      <div className="border-b border-slate-200 dark:border-slate-800 mb-6">
        <nav className="flex -mb-px">
          <TabButton
            active={subTab === "dashboard"}
            onClick={() => onSubTabChange("dashboard")}
          >
            Bảng điều khiển tuân thủ
          </TabButton>
          <TabButton
            active={subTab === "privacy"}
            onClick={() => onSubTabChange("privacy")}
          >
            Cài đặt quyền riêng tư
          </TabButton>
        </nav>
      </div>

      {subTab === "dashboard" ? (
        <div className="space-y-6">
          {loading ? (
            <div className="text-center py-12 text-slate-500 dark:text-slate-400">
              Đang tải báo cáo tuân thủ...
            </div>
          ) : report ? (
            <>
              {/* Stats */}
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
                <StatCard
                  title="Tổng nhật ký kiểm toán"
                  value={report.totalAuditLogs.toLocaleString("vi-VN")}
                  iconColor="text-blue-500"
                  bgColor="bg-blue-500/10"
                />
                <StatCard
                  title="Nhật ký (30 ngày)"
                  value={report.logsLast30Days.toLocaleString("vi-VN")}
                  iconColor="text-green-500"
                  bgColor="bg-green-500/10"
                />
                <StatCard
                  title="Nhật ký (7 ngày)"
                  value={report.logsLast7Days.toLocaleString("vi-VN")}
                  iconColor="text-purple-500"
                  bgColor="bg-purple-500/10"
                />
                <StatCard
                  title="Người dùng duy nhất"
                  value={report.uniqueUsers.toLocaleString("vi-VN")}
                  iconColor="text-orange-500"
                  bgColor="bg-orange-500/10"
                />
              </div>

              {/* Action Type Distribution */}
              <div className="bg-slate-50 dark:bg-slate-800 rounded-lg p-6">
                <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">
                  Phân bố theo loại hành động
                </h3>
                <div className="space-y-2">
                  {Object.entries(report.actionTypeCounts).map(
                    ([action, count]: [string, any]) => (
                      <div
                        key={action}
                        className="flex items-center justify-between"
                      >
                        <span className="text-sm text-slate-700 dark:text-slate-300">
                          {action}
                        </span>
                        <span className="text-sm font-semibold text-slate-900 dark:text-white">
                          {count.toLocaleString("vi-VN")}
                        </span>
                      </div>
                    )
                  )}
                </div>
              </div>

              {/* Resource Type Distribution */}
              <div className="bg-slate-50 dark:bg-slate-800 rounded-lg p-6">
                <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">
                  Phân bố theo loại tài nguyên
                </h3>
                <div className="space-y-2">
                  {Object.entries(report.resourceTypeCounts).map(
                    ([resource, count]: [string, any]) => (
                      <div
                        key={resource}
                        className="flex items-center justify-between"
                      >
                        <span className="text-sm text-slate-700 dark:text-slate-300">
                          {resource}
                        </span>
                        <span className="text-sm font-semibold text-slate-900 dark:text-white">
                          {count.toLocaleString("vi-VN")}
                        </span>
                      </div>
                    )
                  )}
                </div>
              </div>

              {/* Compliance Issues */}
              {report.issues.length > 0 && (
                <div className="bg-yellow-50 dark:bg-yellow-900/20 border border-yellow-200 dark:border-yellow-800 rounded-lg p-6">
                  <h3 className="text-lg font-semibold text-yellow-900 dark:text-yellow-300 mb-4">
                    ⚠️ Vấn đề tuân thủ
                  </h3>
                  <div className="space-y-3">
                    {report.issues.map((issue: any, idx: number) => (
                      <div
                        key={idx}
                        className="bg-white dark:bg-slate-900 rounded-lg p-4 border border-yellow-200 dark:border-yellow-800"
                      >
                        <div className="flex items-start justify-between">
                          <div>
                            <p className="font-medium text-slate-900 dark:text-white">
                              {issue.issueType}
                            </p>
                            <p className="text-sm text-slate-600 dark:text-slate-400 mt-1">
                              {issue.description}
                            </p>
                          </div>
                          <span
                            className={`px-2.5 py-0.5 rounded-full text-xs font-medium ${
                              issue.severity === "High"
                                ? "bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400"
                                : issue.severity === "Medium"
                                ? "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400"
                                : "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400"
                            }`}
                          >
                            {issue.severity === "High"
                              ? "Cao"
                              : issue.severity === "Medium"
                              ? "Trung bình"
                              : "Thấp"}
                          </span>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              <div className="flex justify-end">
                <button
                  onClick={onReload}
                  disabled={loading}
                  className="px-6 py-2 rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-60 transition-colors font-medium"
                >
                  {loading ? "Đang tải..." : "Tải lại"}
                </button>
              </div>
            </>
          ) : (
            <div className="text-center py-12 text-slate-500 dark:text-slate-400">
              Không có dữ liệu báo cáo tuân thủ
            </div>
          )}
        </div>
      ) : (
        <div className="space-y-6">
          {privacySettings ? (
            <>
              <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">
                Cài đặt quyền riêng tư & tuân thủ
              </h3>
              <div className="space-y-4">
                <SettingToggle
                  label="Bật nhật ký kiểm toán"
                  description="Ghi lại tất cả các thao tác trong hệ thống"
                  value={privacySettings.enableAuditLogging}
                  onChange={(v: boolean) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      enableAuditLogging: v,
                    })
                  }
                />
                <SettingNumber
                  label="Thời gian lưu nhật ký kiểm toán (ngày)"
                  description="Số ngày lưu trữ nhật ký kiểm toán trước khi xóa"
                  value={privacySettings.auditLogRetentionDays}
                  onChange={(v: number) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      auditLogRetentionDays: v,
                    })
                  }
                />
                <SettingToggle
                  label="Ẩn danh hóa logs cũ"
                  description="Tự động ẩn danh hóa thông tin nhạy cảm trong logs cũ"
                  value={privacySettings.anonymizeOldLogs}
                  onChange={(v: boolean) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      anonymizeOldLogs: v,
                    })
                  }
                />
                <SettingToggle
                  label="Yêu cầu đồng ý chia sẻ dữ liệu"
                  description="Yêu cầu người dùng đồng ý trước khi chia sẻ dữ liệu"
                  value={privacySettings.requireConsentForDataSharing}
                  onChange={(v: boolean) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      requireConsentForDataSharing: v,
                    })
                  }
                />
                <SettingToggle
                  label="Bật tuân thủ GDPR"
                  description="Tuân thủ các quy định GDPR về bảo vệ dữ liệu"
                  value={privacySettings.enableGdprCompliance}
                  onChange={(v: boolean) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      enableGdprCompliance: v,
                    })
                  }
                />
                <SettingNumber
                  label="Thời gian lưu trữ dữ liệu (ngày)"
                  description="Số ngày lưu trữ dữ liệu người dùng (GDPR: tối đa 7 năm = 2555 ngày)"
                  value={privacySettings.dataRetentionDays}
                  onChange={(v: number) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      dataRetentionDays: v,
                    })
                  }
                />
                <SettingToggle
                  label="Cho phép xuất dữ liệu"
                  description="Cho phép người dùng xuất dữ liệu của họ"
                  value={privacySettings.allowDataExport}
                  onChange={(v: boolean) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      allowDataExport: v,
                    })
                  }
                />
                <SettingToggle
                  label="Yêu cầu 2FA cho thao tác nhạy cảm"
                  description="Yêu cầu xác thực 2 yếu tố cho các thao tác quan trọng"
                  value={privacySettings.requireTwoFactorForSensitiveActions}
                  onChange={(v: boolean) =>
                    onPrivacySettingsChange({
                      ...privacySettings,
                      requireTwoFactorForSensitiveActions: v,
                    })
                  }
                />
              </div>
              <div className="flex gap-3 pt-4 border-t">
                <button
                  onClick={onSave}
                  disabled={saving}
                  className="flex-1 px-4 py-2 rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-60 disabled:cursor-not-allowed transition-colors font-medium"
                >
                  {saving ? "Đang lưu..." : "Lưu cài đặt"}
                </button>
                <button
                  onClick={onReload}
                  disabled={saving}
                  className="px-4 py-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 transition-colors"
                >
                  Hủy
                </button>
              </div>
            </>
          ) : (
            <div className="text-center py-12 text-slate-500 dark:text-slate-400">
              Đang tải cài đặt quyền riêng tư...
            </div>
          )}
        </div>
      )}
    </>
  );
}

function SettingToggle({
  label,
  description,
  value,
  onChange,
}: {
  label: string;
  description: string;
  value: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div className="flex items-start justify-between p-4 bg-slate-50 dark:bg-slate-800 rounded-lg">
      <div className="flex-1">
        <label className="block text-sm font-medium text-slate-900 dark:text-white mb-1">
          {label}
        </label>
        <p className="text-sm text-slate-600 dark:text-slate-400">
          {description}
        </p>
      </div>
      <label className="relative inline-flex items-center cursor-pointer ml-4">
        <input
          type="checkbox"
          className="sr-only peer"
          checked={value}
          onChange={(e) => onChange(e.target.checked)}
        />
        <div className="w-11 h-6 bg-slate-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 dark:peer-focus:ring-blue-800 rounded-full peer dark:bg-slate-700 peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-slate-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all dark:border-slate-600 peer-checked:bg-blue-600"></div>
      </label>
    </div>
  );
}

function SettingNumber({
  label,
  description,
  value,
  onChange,
}: {
  label: string;
  description: string;
  value: number;
  onChange: (v: number) => void;
}) {
  return (
    <div className="p-4 bg-slate-50 dark:bg-slate-800 rounded-lg">
      <label className="block text-sm font-medium text-slate-900 dark:text-white mb-1">
        {label}
      </label>
      <p className="text-sm text-slate-600 dark:text-slate-400 mb-2">
        {description}
      </p>
      <input
        type="number"
        className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-900 text-slate-900 dark:text-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-500"
        value={value}
        onChange={(e) => onChange(Number(e.target.value) || 0)}
        min={0}
      />
    </div>
  );
}
