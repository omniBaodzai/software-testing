import { useState, useRef, useCallback, useEffect } from 'react';
import { useNavigate, Link, useLocation } from 'react-router-dom';
import imageService, { ImageUploadResponse } from '../../services/imageService';
import analysisService from '../../services/analysisService';
import { userPackageService, UserPackage } from '../../services/packageApi';
import toast from 'react-hot-toast';
import { getApiErrorMessage } from '../../utils/getApiErrorMessage';
import PatientHeader from '../../components/patient/PatientHeader';

interface UploadedImage {
  id: string;
  file: File;
  preview: string;
  status: 'uploading' | 'uploaded' | 'error';
  progress: number;
  uploadResponse?: ImageUploadResponse;
  errorMessage?: string;
}

const ImageUploadPage = () => {
  const navigate = useNavigate();
  const location = useLocation();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const startAnalysisInFlightRef = useRef(false);
  const uploadProgressTimersRef = useRef<Record<string, number>>({});
  const [uploadedImages, setUploadedImages] = useState<UploadedImage[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [activePackage, setActivePackage] = useState<UserPackage | null>(null);
  const [loadingPackage, setLoadingPackage] = useState(true);
  const [canUpload, setCanUpload] = useState(false);

  // Load user package on mount and when window gains focus (user might have purchased package in another tab)
  useEffect(() => {
    // Always reload package when component mounts or route changes
    loadUserPackage();
    
    // Reload package when window gains focus (in case user purchased in another tab or completed analysis)
    const handleFocus = () => {
      loadUserPackage();
    };
    window.addEventListener('focus', handleFocus);
    
    // Also reload when page becomes visible (user switches back to tab)
    const handleVisibilityChange = () => {
      if (!document.hidden) {
        // Small delay to ensure backend has processed any pending operations
        setTimeout(() => {
          loadUserPackage();
        }, 500);
      }
    };
    document.addEventListener('visibilitychange', handleVisibilityChange);
    
    // Auto-refresh package info every 15 seconds to keep it updated (especially after analysis)
    const refreshInterval = setInterval(() => {
      loadUserPackage();
    }, 15000);
    
    return () => {
      window.removeEventListener('focus', handleFocus);
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      clearInterval(refreshInterval);
    };
  }, [location.pathname]); // Reload when route changes

  const loadUserPackage = async (showLogs = false) => {
    setLoadingPackage(true);
    try {
      // Force fresh data by calling API directly - bypass any caching
      const packages = await userPackageService.getMyPackages();
      const now = new Date();
      
      // Find active package that is not expired and has remaining credits
      const pkg = packages.find(
        (p) =>
          p.isActive &&
          p.remainingAnalyses > 0 &&
          (!p.expiresAt || new Date(p.expiresAt) > now)
      ) || null;
      
      setActivePackage(pkg);
      
      // Check if package is valid: exists, active, has credits, and not expired
      const isValidPackage = pkg !== null && 
                            pkg.isActive && 
                            pkg.remainingAnalyses > 0 &&
                            (!pkg.expiresAt || new Date(pkg.expiresAt) > now);
      
      setCanUpload(isValidPackage);
      
      if (showLogs) {
        console.log('=== Package Info Reload ===');
        console.log('All packages:', packages);
        console.log('Active package found:', pkg);
        if (!pkg) {
          console.log('No active package found');
        } else if (!pkg.isActive) {
          console.log('Package is not active');
        } else if (pkg.remainingAnalyses === 0) {
          console.log('Package has no remaining analyses');
        } else if (pkg.expiresAt && new Date(pkg.expiresAt) <= now) {
          console.log('Package has expired');
        } else {
          console.log('✅ Package is valid, can upload:', {
            id: pkg.id,
            packageName: pkg.packageName,
            remainingAnalyses: pkg.remainingAnalyses,
            isActive: pkg.isActive,
            expiresAt: pkg.expiresAt
          });
        }
        console.log('========================');
      }
    } catch (error: any) {
      console.error('Error loading user package:', error);
      setCanUpload(false);
      if (showLogs) {
        toast.error('Không thể tải thông tin gói dịch vụ. Vui lòng thử lại.');
      }
    } finally {
      setLoadingPackage(false);
    }
  };

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const formatDate = (dateString?: string): string => {
    if (!dateString) return 'Không giới hạn';
    const date = new Date(dateString);
    return date.toLocaleDateString('vi-VN', {
      year: 'numeric',
      month: 'long',
      day: 'numeric',
    });
  };

  const createPreview = (file: File): Promise<string> => {
    return new Promise((resolve) => {
      const reader = new FileReader();
      reader.onload = (e) => resolve(e.target?.result as string);
      reader.readAsDataURL(file);
    });
  };

  const handleFileSelect = useCallback(async (files: FileList | null) => {
    if (!files || files.length === 0) return;

    // Always get fresh package data from API
    const currentPackage = await userPackageService.getActivePackage();
    
    if (!currentPackage) {
      toast.error('Bạn cần có gói dịch vụ hợp lệ để tải ảnh lên. Vui lòng mua gói dịch vụ trước.', {
        duration: 5000,
      });
      navigate('/packages');
      return;
    }

    // Check if package is active and not expired
    const now = new Date();
    if (!currentPackage.isActive) {
      toast.error('Gói dịch vụ của bạn không còn hoạt động. Vui lòng mua gói mới.', {
        duration: 5000,
      });
      navigate('/packages');
      return;
    }

    if (currentPackage.expiresAt && new Date(currentPackage.expiresAt) <= now) {
      toast.error('Gói dịch vụ của bạn đã hết hạn. Vui lòng mua gói mới.', {
        duration: 5000,
      });
      navigate('/packages');
      return;
    }

    // Check if user has enough credits for the number of images - phải > 0
    if (currentPackage.remainingAnalyses <= 0) {
      toast.error(
        'Bạn đã hết lượt phân tích (số lượt còn lại = 0). Vui lòng mua gói dịch vụ mới để tiếp tục sử dụng.',
        { duration: 6000 }
      );
      navigate('/packages');
      return;
    }

    const imagesToUpload = files.length;
    if (currentPackage.remainingAnalyses < imagesToUpload) {
      toast.error(
        `Bạn chỉ còn ${currentPackage.remainingAnalyses} lượt phân tích. Không đủ để phân tích ${imagesToUpload} ảnh. Vui lòng mua thêm gói dịch vụ.`,
        { duration: 6000 }
      );
      navigate('/packages');
      return;
    }

    // Update state with current package
    setActivePackage(currentPackage);
    setCanUpload(true);

    const newImages: UploadedImage[] = [];

    for (let i = 0; i < files.length; i++) {
      const file = files[i];
      
      // Validate file type
      const validExtensions = ['.jpg', '.jpeg', '.png', '.dicom', '.dcm'];
      const fileExtension = '.' + file.name.split('.').pop()?.toLowerCase();
      if (!validExtensions.includes(fileExtension)) {
        toast.error(`File ${file.name} không được hỗ trợ`);
        continue;
      }

      // Validate file size (50MB)
      if (file.size > 50 * 1024 * 1024) {
        toast.error(`File ${file.name} vượt quá 50MB`);
        continue;
      }

      const preview = await createPreview(file);
      const tempId = `temp-${Date.now()}-${i}`;

      newImages.push({
        id: tempId,
        file,
        preview,
        status: 'uploading',
        progress: 0,
      });
    }

    setUploadedImages((prev) => [...prev, ...newImages]);

    // Upload images
    for (const image of newImages) {
      uploadImage(image);
    }
  }, [navigate]);

  const uploadImage = async (image: UploadedImage) => {
    try {
      setUploadedImages((prev) =>
        prev.map((img) =>
          img.id === image.id ? { ...img, status: 'uploading', progress: 0 } : img
        )
      );

      // Simulate upload progress
      const progressInterval = window.setInterval(() => {
        setUploadedImages((prev) =>
          prev.map((img) =>
            img.id === image.id
              ? { ...img, progress: Math.min(img.progress + 10, 90) }
              : img
          )
        );
      }, 200);
      uploadProgressTimersRef.current[image.id] = progressInterval;

      const response = await imageService.uploadImage(image.file);

      window.clearInterval(progressInterval);
      delete uploadProgressTimersRef.current[image.id];

      setUploadedImages((prev) =>
        prev.map((img) =>
          img.id === image.id
            ? {
                ...img,
                status: 'uploaded',
                progress: 100,
                uploadResponse: response,
              }
            : img
        )
      );
    } catch (error: any) {
      const timer = uploadProgressTimersRef.current[image.id];
      if (timer) {
        window.clearInterval(timer);
        delete uploadProgressTimersRef.current[image.id];
      }

      setUploadedImages((prev) =>
        prev.map((img) =>
          img.id === image.id
            ? {
                ...img,
                status: 'error',
                errorMessage: error.message || 'Lỗi khi tải lên',
              }
            : img
        )
      );
      toast.error(
        `Lỗi khi tải lên ${image.file.name}: ${getApiErrorMessage(error, 'Không thể tải ảnh lên')}`
      );
    }
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(true);
  };

  const handleDragLeave = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    handleFileSelect(e.dataTransfer.files);
  };

  const handleFileInputChange = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files;
    // Only process if files exist
    if (files && files.length > 0) {
      // Process files first (await to ensure it completes)
      await handleFileSelect(files);
      // Reset input value after processing to allow selecting the same file again
      if (e.target) {
        e.target.value = '';
      }
    }
  }, [handleFileSelect]);

  const handleRemoveImage = (id: string) => {
    const timer = uploadProgressTimersRef.current[id];
    if (timer) {
      window.clearInterval(timer);
      delete uploadProgressTimersRef.current[id];
    }
    setUploadedImages((prev) => prev.filter((img) => img.id !== id));
  };

  const handleClearAll = () => {
    Object.values(uploadProgressTimersRef.current).forEach((timer) => window.clearInterval(timer));
    uploadProgressTimersRef.current = {};
    setUploadedImages([]);
  };

  const handleStartAnalysis = async () => {
    // Chặn double-click / gọi trùng request
    if (startAnalysisInFlightRef.current) return;

    // Reload package to ensure latest data
    await loadUserPackage();
    await new Promise(resolve => setTimeout(resolve, 200));
    
    // Re-check package after reload
    const currentPackage = activePackage || await userPackageService.getActivePackage();
    
    // Check package again before starting analysis
    if (!currentPackage) {
      toast.error('Bạn cần có gói dịch vụ hợp lệ để phân tích ảnh. Vui lòng mua gói dịch vụ trước.', {
        duration: 5000,
      });
      navigate('/packages');
      return;
    }

    const now = new Date();
    if (!currentPackage.isActive || (currentPackage.expiresAt && new Date(currentPackage.expiresAt) <= now)) {
      toast.error('Gói dịch vụ của bạn không còn hợp lệ. Vui lòng mua gói mới.', {
        duration: 5000,
      });
      navigate('/packages');
      return;
    }

    const readyImages = uploadedImages.filter(
      (img) => img.status === 'uploaded' && img.uploadResponse
    );

    if (readyImages.length === 0) {
      toast.error('Vui lòng tải lên ít nhất một ảnh');
      return;
    }

    // Check credits again with current package - phải > 0 và đủ số lượt cần thiết
    if (!currentPackage || currentPackage.remainingAnalyses <= 0) {
      toast.error(
        'Bạn đã hết lượt phân tích (số lượt còn lại = 0). Vui lòng mua gói dịch vụ mới để tiếp tục sử dụng.',
        { duration: 6000 }
      );
      navigate('/packages');
      return;
    }

    if (currentPackage.remainingAnalyses < readyImages.length) {
      toast.error(
        `Bạn chỉ còn ${currentPackage.remainingAnalyses} lượt phân tích. Không đủ để phân tích ${readyImages.length} ảnh. Vui lòng mua thêm gói dịch vụ.`,
        { duration: 6000 }
      );
      navigate('/packages');
      return;
    }

    setIsAnalyzing(true);
    startAnalysisInFlightRef.current = true;

    try {
      const imageIds = readyImages
        .map((img) => img.uploadResponse?.id)
        .filter((id): id is string => !!id);

      if (imageIds.length === 0) {
        toast.error('Không tìm thấy ID ảnh hợp lệ sau khi upload');
        return;
      }

      // Show loading toast
      const loadingToast = toast.loading('Đang bắt đầu phân tích AI... (có thể mất 15-30 giây)');
      
      const response = await analysisService.startAnalysis({ imageIds });
      
      toast.dismiss(loadingToast);
      toast.success('Phân tích đã được bắt đầu thành công');

      // Backend deducts credits immediately when analysis starts
      // Reload package info multiple times to ensure we get updated data
      for (let i = 0; i < 3; i++) {
        await new Promise(resolve => setTimeout(resolve, 1000 + i * 500));
        await loadUserPackage(true); // Show logs for debugging
        console.log(`Reload attempt ${i + 1}/3`);
      }

      // Navigate to results page or show results
      if (Array.isArray(response) && response.length > 0) {
        navigate(`/analysis/${response[0].analysisId}`);
      } else if (!Array.isArray(response)) {
        navigate(`/analysis/${response.analysisId}`);
      }
    } catch (error: any) {
      toast.error(
        `Lỗi khi bắt đầu phân tích: ${getApiErrorMessage(error, 'Không thể bắt đầu phân tích')}`
      );
    } finally {
      setIsAnalyzing(false);
      startAnalysisInFlightRef.current = false;
    }
  };

  const readyCount = uploadedImages.filter((img) => img.status === 'uploaded').length;

  return (
    <div className="bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50 font-sans antialiased min-h-screen flex flex-col transition-colors duration-200">
      {/* Header */}
      <PatientHeader />

      {/* Main Content */}
      <main className="flex-grow w-full max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 md:py-12">
        <div className="w-full max-w-3xl mx-auto flex flex-col gap-8">
          {/* Title Section */}
          <div className="flex flex-col gap-4 text-center md:text-left">
            <h1 className="text-3xl md:text-4xl lg:text-5xl font-black leading-tight tracking-tight text-slate-900 dark:text-white">
              Tải ảnh võng mạc
            </h1>
            <p className="text-slate-600 dark:text-slate-400 text-base md:text-lg leading-relaxed max-w-2xl mx-auto md:mx-0">
              Vui lòng tải lên ảnh chụp đáy mắt (Fundus) hoặc ảnh cắt lớp quang học (OCT). AI của
              chúng tôi sẽ phân tích các dấu hiệu sức khỏe mạch máu để phát hiện sớm các nguy cơ
              tiềm ẩn.
            </p>
          </div>

          {/* Package Status Card */}
          {loadingPackage ? (
            <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-6">
              <div className="flex items-center gap-3">
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-blue-600"></div>
                <p className="text-slate-600 dark:text-slate-400">Đang kiểm tra gói dịch vụ...</p>
              </div>
            </div>
          ) : activePackage ? (
            <div className="bg-gradient-to-r from-amber-50/80 to-yellow-50/80 dark:from-amber-900/15 dark:to-yellow-900/15 rounded-xl shadow-sm border border-amber-200 dark:border-amber-800 p-6">
              <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-2">
                    <svg className="w-5 h-5 text-amber-600 dark:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                    <h3 className="font-bold text-lg text-slate-900 dark:text-white">
                      Gói dịch vụ: {activePackage.packageName || 'Đang hoạt động'}
                    </h3>
                  </div>
                  <div className="grid grid-cols-2 md:grid-cols-3 gap-3 text-sm">
                    <div>
                      <p className="text-slate-600 dark:text-slate-400">Lượt còn lại</p>
                      <p className="font-bold text-xl text-amber-600 dark:text-amber-400">
                        {activePackage.remainingAnalyses}
                      </p>
                    </div>
                    {activePackage.expiresAt && (
                      <div>
                        <p className="text-slate-600 dark:text-slate-400">Hết hạn</p>
                        <p className="font-semibold text-slate-900 dark:text-white">
                          {formatDate(activePackage.expiresAt)}
                        </p>
                      </div>
                    )}
                    <div>
                      <p className="text-slate-600 dark:text-slate-400">Trạng thái</p>
                      <p className="font-semibold text-green-600 dark:text-green-400">
                        Đang hoạt động
                      </p>
                    </div>
                  </div>
                </div>
                <div className="flex gap-2">
                  <Link
                    to="/packages"
                    className="px-4 py-2 bg-amber-500 hover:bg-amber-600 text-white rounded-lg text-sm font-semibold transition-colors flex items-center gap-2 shadow-sm border border-amber-600/30"
                    title="Nâng cấp hoặc đổi gói dịch vụ"
                  >
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7h8m0 0v8m0-8l-8 8-4-4-6 6" />
                    </svg>
                    Nâng cấp gói
                  </Link>
                  {activePackage.remainingAnalyses <= 5 && (
                    <Link
                      to="/packages"
                      className="px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white rounded-lg font-medium transition-colors whitespace-nowrap"
                    >
                      Mua thêm gói
                    </Link>
                  )}
                </div>
              </div>
            </div>
          ) : (
            <div className="bg-red-50 dark:bg-red-900/20 rounded-xl shadow-sm border-2 border-red-200 dark:border-red-800 p-6">
              <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                <div className="flex items-start gap-3">
                  <svg className="w-6 h-6 text-red-600 dark:text-red-400 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                  </svg>
                  <div>
                    <h3 className="font-bold text-lg text-red-900 dark:text-red-200 mb-1">
                      Bạn chưa có gói dịch vụ
                    </h3>
                    <p className="text-red-700 dark:text-red-300">
                      Để sử dụng tính năng phân tích ảnh, bạn cần mua một gói dịch vụ. Vui lòng chọn gói phù hợp với nhu cầu của bạn.
                    </p>
                  </div>
                </div>
                <Link
                  to="/packages"
                  className="px-6 py-3 bg-red-600 hover:bg-red-700 text-white rounded-lg font-semibold transition-colors whitespace-nowrap text-center"
                >
                  Xem gói dịch vụ
                </Link>
              </div>
            </div>
          )}

          {/* Upload Zone */}
          <div
            className={`w-full rounded-2xl border-2 border-dashed transition-all duration-300 group relative overflow-hidden bg-white dark:bg-slate-900 ${
              isDragging
                ? 'border-blue-500 bg-blue-50 dark:bg-blue-900/20 shadow-lg'
                : 'border-slate-300 dark:border-slate-700 hover:border-blue-400 dark:hover:border-blue-600 hover:bg-slate-50 dark:hover:bg-slate-800/50 shadow-sm'
            }`}
            onDragOver={handleDragOver}
            onDragLeave={handleDragLeave}
            onDrop={handleDrop}
          >
            <input
              ref={fileInputRef}
              type="file"
              accept=".jpg,.jpeg,.png,.dicom,.dcm"
              multiple
              className="hidden"
              onChange={handleFileInputChange}
            />
            <div className="flex flex-col items-center justify-center gap-6 py-20 px-6 text-center">
              <div className={`size-24 rounded-full bg-blue-50 dark:bg-blue-900/30 flex items-center justify-center text-blue-500 dark:text-blue-400 transition-all duration-300 ${
                isDragging ? 'scale-110 shadow-lg' : 'group-hover:scale-105 shadow-md'
              }`}>
                <span className="material-symbols-outlined text-6xl">cloud_upload</span>
              </div>
              <div className="flex flex-col items-center gap-3">
                <p className="text-xl font-bold leading-tight text-slate-900 dark:text-white">
                  Kéo thả ảnh vào đây hoặc nhấn để chọn
                </p>
                <div className="flex flex-col gap-1 text-slate-600 dark:text-slate-400 text-sm">
                  <p>Hỗ trợ định dạng: <span className="font-semibold text-slate-700 dark:text-slate-300">JPG, PNG, DICOM</span> (Fundus hoặc OCT)</p>
                  <p>Kích thước tối đa: <span className="font-semibold text-slate-700 dark:text-slate-300">50MB/ảnh</span></p>
                </div>
              </div>
              <button 
                type="button"
                onClick={async (e) => {
                  e.preventDefault();
                  e.stopPropagation();
                  
                  // Reload package to ensure latest data
                  await loadUserPackage();
                  await new Promise(resolve => setTimeout(resolve, 200));
                  
                  const currentPackage = activePackage || await userPackageService.getActivePackage();
                  
                  if (!currentPackage) {
                    toast.error('Bạn cần có gói dịch vụ hợp lệ để tải ảnh lên. Vui lòng mua gói dịch vụ trước.', {
                      duration: 5000,
                    });
                    navigate('/packages');
                    return;
                  }

                  const now = new Date();
                  if (!currentPackage.isActive || (currentPackage.expiresAt && new Date(currentPackage.expiresAt) <= now)) {
                    toast.error('Gói dịch vụ của bạn không còn hợp lệ. Vui lòng mua gói mới.', {
                      duration: 5000,
                    });
                    navigate('/packages');
                    return;
                  }

                  if (currentPackage.remainingAnalyses === 0) {
                    toast.error('Gói dịch vụ của bạn đã hết lượt phân tích. Vui lòng mua gói mới.', {
                      duration: 5000,
                    });
                    navigate('/packages');
                    return;
                  }
                  
                  if (fileInputRef.current && !fileInputRef.current.disabled) {
                    fileInputRef.current.click();
                  }
                }}
                disabled={loadingPackage}
                className="bg-blue-600 hover:bg-blue-700 disabled:bg-slate-400 disabled:cursor-not-allowed text-white px-8 py-3.5 rounded-lg text-base font-semibold shadow-lg shadow-blue-500/30 transition-all flex items-center gap-2 relative z-20"
              >
                <span className="material-symbols-outlined text-xl">add_photo_alternate</span>
                {loadingPackage ? 'Đang kiểm tra...' : (activePackage && activePackage.remainingAnalyses > 0 ? 'Chọn ảnh từ thiết bị' : 'Cần mua gói dịch vụ')}
              </button>
            </div>
          </div>

          {/* Selected Images List */}
          {uploadedImages.length > 0 && (
            <div className="flex flex-col gap-6 bg-white dark:bg-slate-900 rounded-2xl p-6 shadow-sm border border-slate-200 dark:border-slate-800">
              <div className="flex items-center justify-between pb-3 border-b border-slate-200 dark:border-slate-700">
                <h3 className="font-bold text-xl text-slate-900 dark:text-white">
                  Ảnh đã chọn <span className="text-blue-600 dark:text-blue-400">({uploadedImages.length})</span>
                </h3>
                <button
                  onClick={handleClearAll}
                  className="text-red-600 dark:text-red-400 text-sm font-semibold hover:text-red-700 dark:hover:text-red-300 hover:underline flex items-center gap-2 transition-colors"
                >
                  <span className="material-symbols-outlined text-lg">delete_sweep</span>
                  Xóa tất cả
                </button>
              </div>

              <div className="flex flex-col gap-4">
                {uploadedImages.map((image) => (
                  <div
                    key={image.id}
                    className="flex flex-col md:flex-row gap-4 p-5 rounded-xl bg-slate-50 dark:bg-slate-800/50 border border-slate-200 dark:border-slate-700 shadow-sm hover:shadow-md transition-all relative overflow-hidden"
                  >
                    {/* Image Thumbnail */}
                    <div className="w-full md:w-24 h-48 md:h-24 shrink-0 rounded-lg overflow-hidden bg-black relative shadow-inner">
                      <div
                        className="absolute inset-0 bg-cover bg-center opacity-90"
                        style={{ backgroundImage: `url(${image.preview})` }}
                      />
                      {image.status === 'uploading' && (
                        <div className="absolute inset-0 flex items-center justify-center bg-black/40 backdrop-blur-[1px]">
                          <span className="material-symbols-outlined text-white animate-spin text-3xl">
                            progress_activity
                          </span>
                        </div>
                      )}
                    </div>

                    {/* Image Info */}
                    <div className="flex flex-1 flex-col justify-center gap-2">
                      <div className="flex justify-between items-center">
                        <div className="min-w-0 flex-1 pr-4">
                          <p className="font-bold text-base truncate text-text-main-light dark:text-text-main-dark">
                            {image.file.name}
                          </p>
                          <p className="text-xs text-text-sub-light dark:text-text-sub-dark mt-0.5">
                            {formatFileSize(image.file.size)} •{' '}
                            {image.status === 'uploading'
                              ? 'Đang tải lên...'
                              : image.status === 'uploaded'
                              ? 'Đã tải lên'
                              : 'Lỗi'}
                          </p>
                        </div>
                        <button
                          onClick={() => handleRemoveImage(image.id)}
                          className="text-text-sub-light dark:text-text-sub-dark hover:text-red-500 transition-colors p-1 rounded-full hover:bg-red-50 dark:hover:bg-red-900/20"
                        >
                          <span className="material-symbols-outlined">
                            {image.status === 'uploading' ? 'close' : 'delete'}
                          </span>
                        </button>
                      </div>

                      {/* Upload Progress */}
                      {image.status === 'uploading' && (
                        <div className="w-full flex flex-col gap-1.5">
                          <div className="flex justify-between text-xs font-semibold">
                            <span className="text-primary">{image.progress}%</span>
                            <span className="text-text-sub-light dark:text-text-sub-dark">
                              Đang tải lên...
                            </span>
                          </div>
                          <div className="h-2 w-full bg-border-light dark:bg-border-dark rounded-full overflow-hidden">
                            <div
                              className="h-full bg-primary rounded-full transition-all duration-300 relative overflow-hidden"
                              style={{ width: `${image.progress}%` }}
                            >
                              <div className="absolute inset-0 bg-white/20 animate-[pulse_2s_cubic-bezier(0.4,0,0.6,1)_infinite]"></div>
                            </div>
                          </div>
                        </div>
                      )}

                      {/* Ready Status */}
                      {image.status === 'uploaded' && (
                        <div className="flex items-center gap-2 mt-2 text-green-600 dark:text-green-500 text-sm font-medium bg-green-50 dark:bg-green-900/20 w-fit px-2 py-1 rounded-md">
                          <span className="material-symbols-outlined text-lg">check_circle</span>
                          Sẵn sàng phân tích
                        </div>
                      )}

                      {/* Error Status */}
                      {image.status === 'error' && (
                        <div className="flex items-center gap-2 mt-2 text-red-600 dark:text-red-500 text-sm font-medium bg-red-50 dark:bg-red-900/20 w-fit px-2 py-1 rounded-md">
                          <span className="material-symbols-outlined text-lg">error</span>
                          {image.errorMessage || 'Lỗi khi tải lên'}
                        </div>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Action Buttons */}
          <div className="flex flex-col gap-6 pt-6 border-t border-slate-200 dark:border-slate-800">
            <div className="flex flex-col sm:flex-row gap-4 w-full">
              <button
                onClick={() => navigate('/dashboard')}
                className="flex-1 px-6 py-3.5 rounded-lg border-2 border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-800 text-slate-700 dark:text-slate-300 font-semibold hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors"
              >
                Hủy bỏ
              </button>
              <button
                onClick={handleStartAnalysis}
                disabled={readyCount === 0 || isAnalyzing || !canUpload || !activePackage}
                className="flex-[2] px-8 py-3.5 rounded-lg bg-blue-600 hover:bg-blue-700 disabled:bg-slate-400 disabled:cursor-not-allowed text-white font-bold shadow-lg shadow-blue-500/30 transition-all flex items-center justify-center gap-2 group"
              >
                {isAnalyzing ? (
                  <>
                    <span className="material-symbols-outlined animate-spin">progress_activity</span>
                    <span>Đang xử lý AI... (vui lòng đợi)</span>
                  </>
                ) : (
                  <>
                    <span className="material-symbols-outlined group-hover:animate-pulse">analytics</span>
                    <span>Bắt đầu phân tích</span>
                  </>
                )}
              </button>
            </div>
            
            {/* Privacy Statement */}
            <div className="flex items-center justify-center gap-2 text-slate-500 dark:text-slate-400 text-xs bg-slate-50 dark:bg-slate-800/50 py-3 px-5 rounded-lg border border-slate-200 dark:border-slate-700">
              <span className="material-symbols-outlined text-base text-green-600 dark:text-green-400">
                lock
              </span>
              <span>Dữ liệu của bạn được mã hóa an toàn & tuân thủ chuẩn HIPAA.</span>
            </div>
          </div>
        </div>
      </main>
    </div>
  );
};

export default ImageUploadPage;

