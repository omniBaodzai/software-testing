import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, Link } from "react-router-dom";
import toast from "react-hot-toast";
import ClinicHeader from "../../components/clinic/ClinicHeader";
import clinicAuthService from "../../services/clinicAuthService";
import clinicImageService, { ClinicBulkUploadResponse } from "../../services/clinicImageService";
import clinicPackageService, { CurrentPackage } from "../../services/clinicPackageService";
import clinicManagementService, { ClinicPatient } from "../../services/clinicManagementService";
import { getApiErrorMessage } from "../../utils/getApiErrorMessage";

interface SelectedImage {
  id: string;
  file: File;
  preview: string;
  status: "idle" | "uploading" | "uploaded" | "error";
  progress: number;
}

/**
 * Trang upload + phân tích AI cho clinic. Luồng 100% giống patient:
 * 1) Chọn ảnh → 2) Bắt đầu phân tích (upload + gọi analysis/start) → 3) Redirect sang trang kết quả theo analysisId.
 */
const ClinicUploadPage = () => {
  const navigate = useNavigate();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const startAnalysisInFlightRef = useRef(false);
  const [isDragging, setIsDragging] = useState(false);
  const [selected, setSelected] = useState<SelectedImage[]>([]);
  const [uploading, setUploading] = useState(false);
  const [activePackage, setActivePackage] = useState<CurrentPackage | null>(null);
  const [loadingPackage, setLoadingPackage] = useState(true);
  const [canUpload, setCanUpload] = useState(false);
  const [patients, setPatients] = useState<ClinicPatient[]>([]);
  const [loadingPatients, setLoadingPatients] = useState(true);
  const [selectedPatientId, setSelectedPatientId] = useState<string>("");

  useEffect(() => {
    (async () => {
      const ok = await clinicAuthService.ensureLoggedIn();
      if (!ok) navigate("/login");
    })();
  }, [navigate]);

  useEffect(() => {
    loadPackage();
  }, []);

  // Refetch package when page gains focus (sau khi phân tích xong quay lại, lượt còn lại cập nhật mượt)
  useEffect(() => {
    const onFocus = () => {
      if (clinicAuthService.isLoggedIn()) loadPackage();
    };
    window.addEventListener("focus", onFocus);
    return () => window.removeEventListener("focus", onFocus);
  }, []);

  useEffect(() => {
    (async () => {
      if (!clinicAuthService.isLoggedIn()) return;
      setLoadingPatients(true);
      try {
        const list = await clinicManagementService.getPatients();
        setPatients(list ?? []);
      } catch {
        setPatients([]);
      } finally {
        setLoadingPatients(false);
      }
    })();
  }, []);

  const loadPackage = async () => {
    setLoadingPackage(true);
    try {
      const pkg = await clinicPackageService.getCurrentPackage();
      setActivePackage(pkg ?? null);
      const now = new Date();
      const valid =
        pkg &&
        pkg.isActive &&
        pkg.remainingAnalyses > 0 &&
        (!pkg.expiresAt || new Date(pkg.expiresAt) > now);
      setCanUpload(!!valid);
    } catch {
      setCanUpload(false);
    } finally {
      setLoadingPackage(false);
    }
  };

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const createPreview = (file: File): Promise<string> =>
    new Promise((resolve) => {
      const reader = new FileReader();
      reader.onload = (e) => resolve(String(e.target?.result ?? ""));
      reader.readAsDataURL(file);
    });

  const validateFiles = (files: FileList | null): File[] => {
    if (!files?.length) return [];
    const validExtensions = [".jpg", ".jpeg", ".png", ".dicom", ".dcm"];
    const maxSize = 50 * 1024 * 1024;
    const out: File[] = [];
    for (let i = 0; i < files.length; i++) {
      const f = files[i];
      const ext = "." + f.name.split(".").pop()?.toLowerCase();
      if (!validExtensions.includes(ext)) {
        toast.error(`File ${f.name} không được hỗ trợ (JPG/PNG/DICOM)`);
        continue;
      }
      if (f.size > maxSize) {
        toast.error(`File ${f.name} vượt quá 50MB`);
        continue;
      }
      out.push(f);
    }
    return out;
  };

  const addFiles = useCallback(async (files: FileList | null) => {
    const valid = validateFiles(files);
    if (valid.length === 0) return;
    const newItems: SelectedImage[] = [];
    for (let i = 0; i < valid.length; i++) {
      const file = valid[i];
      const preview = await createPreview(file);
      newItems.push({
        id: `sel-${Date.now()}-${i}`,
        file,
        preview,
        status: "idle",
        progress: 0,
      });
    }
    setSelected((prev) => [...prev, ...newItems]);
  }, []);

  const onPickFiles = async (e: React.ChangeEvent<HTMLInputElement>) => {
    await addFiles(e.target.files);
    e.target.value = "";
  };

  const onDrop = async (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    await addFiles(e.dataTransfer.files);
  };

  const removeOne = (id: string) => setSelected((prev) => prev.filter((x) => x.id !== id));
  const clearAll = () => setSelected([]);

  /** Giống patient: upload (bulk, không auto start) → startAnalysis(imageIds) → redirect theo analysisId */
  const startUploadAndAnalyze = async () => {
    if (startAnalysisInFlightRef.current) return;
    if (selected.length === 0) {
      toast.error("Vui lòng chọn ít nhất một ảnh");
      return;
    }
    await loadPackage();
    const pkg = activePackage ?? (await clinicPackageService.getCurrentPackage());
    if (!pkg || !pkg.isActive || pkg.remainingAnalyses <= 0) {
      toast.error("Phòng khám cần có gói dịch vụ hợp lệ. Vui lòng mua gói tại Gói dịch vụ.");
      navigate("/clinic/packages");
      return;
    }
    if (pkg.remainingAnalyses < selected.length) {
      toast.error(
        `Chỉ còn ${pkg.remainingAnalyses} lượt phân tích, không đủ cho ${selected.length} ảnh.`
      );
      navigate("/clinic/packages");
      return;
    }

    setUploading(true);
    startAnalysisInFlightRef.current = true;

    try {
      // Bước 1: Upload ảnh (không bật autoStartAnalysis – dùng API analysis/start riêng giống patient)
      const loadingToast = toast.loading("Đang tải ảnh lên...");
      const files = selected.map((s) => s.file);
      const uploadResult: ClinicBulkUploadResponse = await clinicImageService.bulkUploadImages(
        files,
        {
          autoStartAnalysis: false,
          patientUserId: selectedPatientId || undefined,
        }
      );
      toast.dismiss(loadingToast);

      if (!uploadResult.successfullyUploaded?.length) {
        const firstError = uploadResult.failed?.[0];
        const reason = firstError
          ? `${firstError.filename}: ${firstError.errorMessage}`
          : "Máy chủ không trả về chi tiết lỗi.";
        toast.error(`Không có ảnh nào upload thành công. ${reason}`);
        return;
      }

      const imageIds = uploadResult.successfullyUploaded.map((x) => x.id).filter(Boolean);
      if (imageIds.length === 0) {
        toast.error("Không lấy được ID ảnh sau khi upload.");
        return;
      }

      // Bước 2: Gọi start analysis (giống patient) – trả về analysisId
      toast.loading("Đang bắt đầu phân tích AI... (có thể mất 15–30 giây)", { id: "analysis-start" });
      const response = await clinicImageService.startAnalysis({ imageIds });
      toast.dismiss("analysis-start");
      toast.success("Phân tích đã được bắt đầu thành công");

      // Bước 3: Redirect sang trang kết quả theo analysisId (giống patient)
      const analysisId = Array.isArray(response) && response.length > 0
        ? response[0].analysisId
        : (response as { analysisId: string }).analysisId;
      if (analysisId) {
        navigate(`/clinic/analysis/result/${analysisId}`);
      } else {
        toast.error("Không nhận được mã kết quả phân tích.");
      }
      await loadPackage();
    } catch (err: any) {
      toast.error(getApiErrorMessage(err, "Không thể upload hoặc bắt đầu phân tích"));
    } finally {
      setUploading(false);
      startAnalysisInFlightRef.current = false;
    }
  };

  return (
    <div className="bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50 font-sans antialiased min-h-screen flex flex-col transition-colors duration-200">
      <ClinicHeader />

      <main className="flex-grow w-full max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 md:py-12">
        <div className="w-full max-w-3xl mx-auto flex flex-col gap-8">
          <div className="flex flex-col gap-4 text-center md:text-left">
            <h1 className="text-3xl md:text-4xl lg:text-5xl font-black leading-tight tracking-tight text-slate-900 dark:text-white">
              Tải ảnh võng mạc
            </h1>
            <p className="text-slate-600 dark:text-slate-400 text-base md:text-lg leading-relaxed max-w-2xl mx-auto md:mx-0">
              Vui lòng tải lên ảnh chụp đáy mắt (Fundus) hoặc ảnh cắt lớp quang học (OCT). AI sẽ
              phân tích các dấu hiệu sức khỏe mạch máu để phát hiện sớm các nguy cơ tiềm ẩn.
            </p>
          </div>

          {loadingPackage ? (
            <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-6">
              <div className="flex items-center gap-3">
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-blue-600" />
                <p className="text-slate-600 dark:text-slate-400">Đang kiểm tra gói dịch vụ...</p>
              </div>
            </div>
          ) : activePackage && activePackage.remainingAnalyses > 0 ? (
            <div className="bg-gradient-to-r from-blue-50 to-indigo-50 dark:from-blue-900/20 dark:to-indigo-900/20 rounded-xl shadow-sm border border-blue-200 dark:border-blue-800 p-6">
              <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-2">
                    <svg className="w-5 h-5 text-blue-600 dark:text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                    <h3 className="font-bold text-lg text-slate-900 dark:text-white">
                      Gói dịch vụ: {activePackage.packageName || "Đang hoạt động"}
                    </h3>
                  </div>
                  <div className="grid grid-cols-2 md:grid-cols-3 gap-3 text-sm">
                    <div>
                      <p className="text-slate-600 dark:text-slate-400">Lượt còn lại</p>
                      <p className="font-bold text-xl text-blue-600 dark:text-blue-400">{activePackage.remainingAnalyses}</p>
                    </div>
                    <div>
                      <p className="text-slate-600 dark:text-slate-400">Trạng thái</p>
                      <p className="font-semibold text-green-600 dark:text-green-400">Đang hoạt động</p>
                    </div>
                  </div>
                </div>
                <div className="flex gap-2">
                  <button
                    onClick={loadPackage}
                    disabled={loadingPackage}
                    className="px-3 py-1.5 bg-slate-100 dark:bg-slate-800 hover:bg-slate-200 dark:hover:bg-slate-700 text-slate-700 dark:text-slate-300 rounded-lg text-sm font-medium transition-colors disabled:opacity-50 flex items-center gap-1.5"
                  >
                    <span className="material-symbols-outlined text-lg">refresh</span>
                    Làm mới
                  </button>
                  <Link to="/clinic/packages" className="px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white rounded-lg font-medium whitespace-nowrap">
                    Gói dịch vụ
                  </Link>
                </div>
              </div>
            </div>
          ) : (
            <div className="bg-red-50 dark:bg-red-900/20 rounded-xl shadow-sm border-2 border-red-200 dark:border-red-800 p-6">
              <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                <div className="flex items-start gap-3">
                  <span className="material-symbols-outlined text-red-600 dark:text-red-400 text-2xl">warning</span>
                  <div>
                    <h3 className="font-bold text-lg text-red-900 dark:text-red-200 mb-1">Chưa có gói dịch vụ hoặc đã hết lượt</h3>
                    <p className="text-red-700 dark:text-red-300">Để phân tích ảnh, phòng khám cần mua hoặc gia hạn gói dịch vụ.</p>
                  </div>
                </div>
                <Link to="/clinic/packages" className="px-6 py-3 bg-red-600 hover:bg-red-700 text-white rounded-lg font-semibold whitespace-nowrap text-center">
                  Xem gói dịch vụ
                </Link>
              </div>
            </div>
          )}

          {/* Gán ảnh cho bệnh nhân - để quản lý và xem lại theo từng bệnh nhân */}
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-6">
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-2 flex items-center gap-2">
              <span className="material-symbols-outlined text-indigo-600 dark:text-indigo-400">person</span>
              Gán ảnh cho bệnh nhân
            </h3>
            <p className="text-sm text-slate-600 dark:text-slate-400 mb-4">
              Chọn bệnh nhân trước khi tải ảnh để lưu kết quả phân tích đúng hồ sơ, dễ quản lý và xem lại theo từng bệnh nhân.
            </p>
            {loadingPatients ? (
              <div className="flex items-center gap-2 text-slate-500 dark:text-slate-400 text-sm">
                <div className="animate-spin rounded-full h-4 w-4 border-2 border-indigo-500 border-t-transparent" />
                Đang tải danh sách bệnh nhân...
              </div>
            ) : (
              <div className="flex flex-col sm:flex-row sm:items-center gap-3">
                <label htmlFor="patient-select" className="text-sm font-medium text-slate-700 dark:text-slate-300 shrink-0">
                  Bệnh nhân:
                </label>
                <select
                  id="patient-select"
                  value={selectedPatientId}
                  onChange={(e) => setSelectedPatientId(e.target.value)}
                  className="flex-1 max-w-md px-4 py-2.5 rounded-lg border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-800 text-slate-900 dark:text-white focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                >
                  <option value="">— Không gán (upload chung) —</option>
                  {patients.map((p) => (
                    <option key={p.id} value={p.userId}>
                      {p.fullName} {p.email ? `(${p.email})` : ""}
                    </option>
                  ))}
                </select>
                {patients.length === 0 && (
                  <Link
                    to="/clinic/patients"
                    className="text-sm font-medium text-indigo-600 dark:text-indigo-400 hover:underline shrink-0"
                  >
                    Thêm bệnh nhân →
                  </Link>
                )}
              </div>
            )}
          </div>

          <div
            className={`w-full rounded-2xl border-2 border-dashed transition-all duration-300 group relative overflow-hidden bg-white dark:bg-slate-900 ${
              isDragging ? "border-blue-500 bg-blue-50 dark:bg-blue-900/20 shadow-lg" : "border-slate-300 dark:border-slate-700 hover:border-blue-400 dark:hover:border-blue-600 hover:bg-slate-50 dark:hover:bg-slate-800/50 shadow-sm"
            }`}
            onDragOver={(e) => { e.preventDefault(); setIsDragging(true); }}
            onDragLeave={(e) => { e.preventDefault(); setIsDragging(false); }}
            onDrop={onDrop}
          >
            <input ref={fileInputRef} type="file" accept=".jpg,.jpeg,.png,.dicom,.dcm" multiple className="hidden" onChange={onPickFiles} />
            <div className="flex flex-col items-center justify-center gap-6 py-20 px-6 text-center">
              <div className={`size-24 rounded-full bg-blue-50 dark:bg-blue-900/30 flex items-center justify-center text-blue-500 dark:text-blue-400 transition-all duration-300 ${isDragging ? "scale-110 shadow-lg" : "group-hover:scale-105 shadow-md"}`}>
                <span className="material-symbols-outlined text-6xl">cloud_upload</span>
              </div>
              <div className="flex flex-col items-center gap-3">
                <p className="text-xl font-bold leading-tight text-slate-900 dark:text-white">Kéo thả ảnh vào đây hoặc nhấn để chọn</p>
                <div className="flex flex-col gap-1 text-slate-600 dark:text-slate-400 text-sm">
                  <p>Hỗ trợ định dạng: <span className="font-semibold text-slate-700 dark:text-slate-300">JPG, PNG, DICOM</span> (Fundus hoặc OCT)</p>
                  <p>Kích thước tối đa: <span className="font-semibold text-slate-700 dark:text-slate-300">50MB/ảnh</span></p>
                </div>
              </div>
              <button
                type="button"
                onClick={() => fileInputRef.current?.click()}
                disabled={loadingPackage || !canUpload}
                className="bg-blue-600 hover:bg-blue-700 disabled:bg-slate-400 disabled:cursor-not-allowed text-white px-8 py-3.5 rounded-lg text-base font-semibold shadow-lg shadow-blue-500/30 transition-all flex items-center gap-2"
              >
                <span className="material-symbols-outlined text-xl">add_photo_alternate</span>
                {loadingPackage ? "Đang kiểm tra..." : canUpload ? "Chọn ảnh từ thiết bị" : "Cần mua gói dịch vụ"}
              </button>
            </div>
          </div>

          {selected.length > 0 && (
            <div className="flex flex-col gap-6 bg-white dark:bg-slate-900 rounded-2xl p-6 shadow-sm border border-slate-200 dark:border-slate-800">
              <div className="flex items-center justify-between pb-3 border-b border-slate-200 dark:border-slate-700">
                <h3 className="font-bold text-xl text-slate-900 dark:text-white">
                  Ảnh đã chọn <span className="text-blue-600 dark:text-blue-400">({selected.length})</span>
                </h3>
                <button onClick={clearAll} className="text-red-600 dark:text-red-400 text-sm font-semibold hover:underline flex items-center gap-2">
                  <span className="material-symbols-outlined text-lg">delete_sweep</span>
                  Xóa tất cả
                </button>
              </div>
              <div className="flex flex-col gap-4">
                {selected.map((img) => (
                  <div key={img.id} className="flex flex-col md:flex-row gap-4 p-5 rounded-xl bg-slate-50 dark:bg-slate-800/50 border border-slate-200 dark:border-slate-700">
                    <div className="w-full md:w-24 h-48 md:h-24 shrink-0 rounded-lg overflow-hidden bg-black relative">
                      <div className="absolute inset-0 bg-cover bg-center opacity-90" style={{ backgroundImage: `url(${img.preview})` }} />
                    </div>
                    <div className="flex flex-1 flex-col justify-center gap-2">
                      <div className="flex justify-between items-center">
                        <div className="min-w-0 flex-1 pr-4">
                          <p className="font-bold text-base truncate text-slate-900 dark:text-white">{img.file.name}</p>
                          <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">{formatFileSize(img.file.size)}</p>
                        </div>
                        <button onClick={() => removeOne(img.id)} className="text-slate-500 hover:text-red-500 transition-colors p-1 rounded-full hover:bg-red-50 dark:hover:bg-red-900/20">
                          <span className="material-symbols-outlined">close</span>
                        </button>
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          <div className="flex flex-col gap-6 pt-6 border-t border-slate-200 dark:border-slate-800">
            <div className="flex flex-col sm:flex-row gap-4 w-full">
              <button
                onClick={() => navigate("/clinic/dashboard")}
                className="flex-1 px-6 py-3.5 rounded-lg border-2 border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-800 text-slate-700 dark:text-slate-300 font-semibold hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors"
              >
                Hủy bỏ
              </button>
              <button
                onClick={startUploadAndAnalyze}
                disabled={selected.length === 0 || uploading || !canUpload || !activePackage?.remainingAnalyses}
                className="flex-[2] px-8 py-3.5 rounded-lg bg-blue-600 hover:bg-blue-700 disabled:bg-slate-400 disabled:cursor-not-allowed text-white font-bold shadow-lg shadow-blue-500/30 transition-all flex items-center justify-center gap-2"
              >
                {uploading ? (
                  <>
                    <span className="material-symbols-outlined animate-spin">progress_activity</span>
                    <span>Đang xử lý AI... (vui lòng đợi)</span>
                  </>
                ) : (
                  <>
                    <span className="material-symbols-outlined">analytics</span>
                    <span>Bắt đầu phân tích</span>
                  </>
                )}
              </button>
            </div>
            <div className="flex items-center justify-center gap-2 text-slate-500 dark:text-slate-400 text-xs bg-slate-50 dark:bg-slate-800/50 py-3 px-5 rounded-lg border border-slate-200 dark:border-slate-700">
              <span className="material-symbols-outlined text-base text-green-600 dark:text-green-400">lock</span>
              <span>Dữ liệu được mã hóa an toàn & tuân thủ chuẩn HIPAA.</span>
            </div>
          </div>
        </div>
      </main>
    </div>
  );
};

export default ClinicUploadPage;
