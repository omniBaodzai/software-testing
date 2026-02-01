import { useState, useEffect, useMemo } from 'react';
import { Link, useNavigate, useLocation } from 'react-router-dom';
import { useAuthStore } from '../../store/authStore';
import messageService from '../../services/messageService';
import analysisService, { AnalysisResult } from '../../services/analysisService';
import imageService from '../../services/imageService';
import medicalNotesService, { MedicalNote } from '../../services/medicalNotesService';
import PatientHeader from '../../components/patient/PatientHeader';
import toast from 'react-hot-toast';

interface DashboardHealthData {
  healthScore: number;
  lastAnalysisDate: string | null;
  status: 'stable' | 'warning' | 'critical';
  riskAssessment: 'low' | 'medium' | 'high' | 'critical';
  cardiovascular: {
    status: 'good' | 'warning' | 'critical';
    title: string;
    description: string;
  };
  diabeticRetinopathy: {
    status: 'good' | 'warning' | 'critical';
    title: string;
    description: string;
  };
  stroke: {
    status: 'good' | 'warning' | 'critical';
    title: string;
    description: string;
  };
  healthHistory: Array<{ month: string; score: number }>;
  recentReports: Array<{ id: string; title: string; date: string; risk: string }>;
  latestImageUrl?: string;
}

const PatientDashboard = () => {
  const { user } = useAuthStore();
  const navigate = useNavigate();
  const location = useLocation();
  const [selectedPeriod, setSelectedPeriod] = useState('6months');
  const [unreadCount, setUnreadCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [healthData, setHealthData] = useState<DashboardHealthData | null>(null);
  const [analysisResults, setAnalysisResults] = useState<AnalysisResult[]>([]);
  const [latestImageUrl, setLatestImageUrl] = useState<string | undefined>();
  const [recentNotes, setRecentNotes] = useState<MedicalNote[]>([]);
  const [loadingNotes, setLoadingNotes] = useState(true);

  // Load dashboard data
  useEffect(() => {
    loadDashboardData();
    
    // Reload data when route changes
    messageService.getUnreadCount()
      .then(count => setUnreadCount(count))
      .catch(() => {});

    // Load recent medical notes
    setLoadingNotes(true);
    medicalNotesService.getMyNotes()
      .then(notes => {
        // Get most recent 3 notes
        const sorted = notes.sort((a, b) => 
          new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
        );
        setRecentNotes(sorted.slice(0, 3));
      })
      .catch(() => setRecentNotes([]))
      .finally(() => setLoadingNotes(false));
    
    // Refresh unread count every 30 seconds
    const interval = setInterval(() => {
      messageService.getUnreadCount()
        .then(count => setUnreadCount(count))
        .catch(() => {});
    }, 30000);

    return () => clearInterval(interval);
  }, [location.pathname]);

  const loadDashboardData = async () => {
    try {
      setLoading(true);
      
      // Load analysis results
      const results = await analysisService.getUserAnalysisResults();
      setAnalysisResults(results);
      
      // Calculate health data from results
      const calculatedData = calculateHealthData(results);
      setHealthData(calculatedData);
      
      // Load latest image URL separately
      if (results.length > 0) {
        const latestResult = results
          .filter(r => r.analysisStatus === 'Completed')
          .sort((a, b) => {
            const dateA = a.analysisCompletedAt ? new Date(a.analysisCompletedAt).getTime() : 0;
            const dateB = b.analysisCompletedAt ? new Date(b.analysisCompletedAt).getTime() : 0;
            return dateB - dateA;
          })[0];
        
        if (latestResult?.imageId) {
          imageService.getUserImages()
            .then(images => {
              const latestImage = images.find(img => img.id === latestResult.imageId);
              if (latestImage?.cloudinaryUrl) {
                setLatestImageUrl(latestImage.cloudinaryUrl);
              }
            })
            .catch(() => {});
        }
      }
    } catch (error: any) {
      console.error('Error loading dashboard data:', error);
      toast.error('Không thể tải dữ liệu dashboard');
      
      // Set empty state
      setHealthData({
        healthScore: 0,
        lastAnalysisDate: null,
        status: 'stable',
        riskAssessment: 'low',
        cardiovascular: {
          status: 'good',
          title: 'Chưa có dữ liệu',
          description: 'Chưa có phân tích nào được thực hiện.'
        },
        diabeticRetinopathy: {
          status: 'good',
          title: 'Chưa có dữ liệu',
          description: 'Chưa có phân tích nào được thực hiện.'
        },
        stroke: {
          status: 'good',
          title: 'Chưa có dữ liệu',
          description: 'Chưa có phân tích nào được thực hiện.'
        },
        healthHistory: [],
        recentReports: []
      });
    } finally {
      setLoading(false);
    }
  };

  // Calculate health data from analysis results
  const calculateHealthData = (results: AnalysisResult[]): DashboardHealthData => {
    // Filter completed results only
    const completedResults = results.filter(r => r.analysisStatus === 'Completed');
    
    if (completedResults.length === 0) {
      return {
        healthScore: 0,
        lastAnalysisDate: null,
        status: 'stable',
        riskAssessment: 'low',
        cardiovascular: {
          status: 'good',
          title: 'Chưa có dữ liệu',
          description: 'Chưa có phân tích nào được thực hiện. Hãy tải ảnh lên để bắt đầu.'
        },
        diabeticRetinopathy: {
          status: 'good',
          title: 'Chưa có dữ liệu',
          description: 'Chưa có phân tích nào được thực hiện.'
        },
        stroke: {
          status: 'good',
          title: 'Chưa có dữ liệu',
          description: 'Chưa có phân tích nào được thực hiện.'
        },
        healthHistory: [],
        recentReports: []
      };
    }

    // Get latest result
    const latestResult = completedResults.sort((a, b) => {
      const dateA = a.analysisCompletedAt ? new Date(a.analysisCompletedAt).getTime() : 0;
      const dateB = b.analysisCompletedAt ? new Date(b.analysisCompletedAt).getTime() : 0;
      return dateB - dateA;
    })[0];

    // Calculate health score (inverse of risk score, normalized to 0-100)
    const riskScore = latestResult.riskScore ?? 0;
    // Round to 1 decimal place to avoid floating point precision issues
    const healthScore = Math.round((Math.max(0, Math.min(100, 100 - riskScore)) * 10)) / 10;

    // Determine status based on risk level
    let status: 'stable' | 'warning' | 'critical' = 'stable';
    if (latestResult.overallRiskLevel === 'High' || latestResult.overallRiskLevel === 'Critical') {
      status = 'critical';
    } else if (latestResult.overallRiskLevel === 'Medium') {
      status = 'warning';
    }

    // Calculate cardiovascular risk
    const cardiovascularRisk = latestResult.hypertensionRisk || 'Low';
    const cardiovascularScore = latestResult.hypertensionScore ?? 0;
    const cardiovascular = {
      status: cardiovascularRisk === 'High' ? 'critical' : cardiovascularRisk === 'Medium' ? 'warning' : 'good' as const,
      title: cardiovascularRisk === 'High' ? 'Rủi ro cao' : cardiovascularRisk === 'Medium' ? 'Rủi ro trung bình' : 'Rủi ro thấp',
      description: cardiovascularRisk === 'High' 
        ? `Điểm rủi ro tim mạch: ${cardiovascularScore.toFixed(1)}/100. Cần theo dõi và tư vấn bác sĩ.`
        : cardiovascularRisk === 'Medium'
        ? `Điểm rủi ro tim mạch: ${cardiovascularScore.toFixed(1)}/100. Nên theo dõi định kỳ.`
        : 'Kích thước mạch máu trong giới hạn bình thường.'
    };

    // Calculate diabetic retinopathy
    const diabetesRisk = latestResult.diabetesRisk || 'Low';
    const diabetesScore = latestResult.diabetesScore ?? 0;
    const diabeticRetinopathy = {
      status: latestResult.diabeticRetinopathyDetected ? 'critical' : diabetesRisk === 'High' ? 'critical' : diabetesRisk === 'Medium' ? 'warning' : 'good' as const,
      title: latestResult.diabeticRetinopathyDetected 
        ? 'Phát hiện võng mạc đái tháo đường'
        : diabetesRisk === 'High' 
        ? 'Rủi ro cao'
        : diabetesRisk === 'Medium'
        ? 'Rủi ro trung bình'
        : 'Không phát hiện',
      description: latestResult.diabeticRetinopathyDetected
        ? `Phát hiện võng mạc đái tháo đường (Mức độ: ${latestResult.diabeticRetinopathySeverity || 'Chưa xác định'}). Cần tư vấn bác sĩ ngay.`
        : diabetesRisk === 'High'
        ? `Điểm rủi ro đái tháo đường: ${diabetesScore.toFixed(1)}/100. Cần theo dõi.`
        : diabetesRisk === 'Medium'
        ? `Điểm rủi ro đái tháo đường: ${diabetesScore.toFixed(1)}/100. Nên kiểm tra định kỳ.`
        : 'Không tìm thấy vi phình mạch.'
    };

    // Calculate stroke risk
    const strokeRisk = latestResult.strokeRisk || 'Low';
    const strokeScore = latestResult.strokeScore ?? 0;
    const stroke = {
      status: strokeRisk === 'High' ? 'critical' : strokeRisk === 'Medium' ? 'warning' : 'good' as const,
      title: strokeRisk === 'High' ? 'Rủi ro cao' : strokeRisk === 'Medium' ? 'Rủi ro trung bình' : 'Rủi ro thấp',
      description: strokeRisk === 'High'
        ? `Điểm rủi ro đột quỵ: ${strokeScore.toFixed(1)}/100. Cần theo dõi và tư vấn bác sĩ ngay.`
        : strokeRisk === 'Medium'
        ? `Điểm rủi ro đột quỵ: ${strokeScore.toFixed(1)}/100. Nên theo dõi định kỳ.`
        : 'Không phát hiện dấu hiệu rủi ro đột quỵ.'
    };

    // Get recent reports (last 3)
    const recentReports = completedResults
      .slice(0, 3)
      .map(r => ({
        id: r.id,
        title: `Phân tích ${r.analysisCompletedAt ? new Date(r.analysisCompletedAt).toLocaleDateString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' }) : ''}`,
        date: r.analysisCompletedAt 
          ? new Date(r.analysisCompletedAt).toLocaleDateString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' })
          : 'N/A',
        risk: (r.overallRiskLevel || 'Low').toLowerCase()
      }));

    // Format last analysis date
    const lastAnalysisDate = latestResult.analysisCompletedAt
      ? new Date(latestResult.analysisCompletedAt).toLocaleDateString('vi-VN', {
          timeZone: 'Asia/Ho_Chi_Minh',
          year: 'numeric',
          month: 'long',
          day: 'numeric'
        })
      : null;

    return {
      healthScore,
      lastAnalysisDate,
      status,
      riskAssessment: (latestResult.overallRiskLevel?.toLowerCase() || 'low') as 'low' | 'medium' | 'high' | 'critical',
      cardiovascular: cardiovascular as { status: 'good' | 'warning' | 'critical'; title: string; description: string },
      diabeticRetinopathy: diabeticRetinopathy as { status: 'good' | 'warning' | 'critical'; title: string; description: string },
      stroke: stroke as { status: 'good' | 'warning' | 'critical'; title: string; description: string },
      healthHistory: [], // Will be calculated separately
      recentReports
    };
  };

  // Calculate health history from analysis results
  const calculateHealthHistory = (results: AnalysisResult[], period: string): Array<{ month: string; score: number }> => {
    if (!results || results.length === 0) return [];
    
    const now = new Date();
    const monthsAgo = period === '1year' ? 12 : 6;
    const startDate = new Date(now.getFullYear(), now.getMonth() - monthsAgo, 1);

    // Filter results within period - include all results if they're recent enough
    const periodResults = results.filter(r => {
      if (!r.analysisCompletedAt) return false;
      try {
        const resultDate = new Date(r.analysisCompletedAt);
        return resultDate >= startDate && !isNaN(resultDate.getTime());
      } catch {
        return false;
      }
    });

    // Use period results if available, otherwise use all valid results
    const resultsToUse = periodResults.length > 0 ? periodResults : results.filter(r => {
      if (!r.analysisCompletedAt) return false;
      try {
        const resultDate = new Date(r.analysisCompletedAt);
        return !isNaN(resultDate.getTime());
      } catch {
        return false;
      }
    });

    if (resultsToUse.length === 0) return [];

    // Group by month and calculate average health score
    const monthlyData: Record<string, number[]> = {};
    
    results.forEach(r => {
      if (!r.analysisCompletedAt) return;
      try {
        const date = new Date(r.analysisCompletedAt);
        if (isNaN(date.getTime())) return;
        
        const monthKey = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
        const riskScore = r.riskScore ?? 0;
        // Round to 1 decimal place to avoid floating point precision issues
        const healthScore = Math.round((Math.max(0, Math.min(100, 100 - riskScore)) * 10)) / 10;
        
        if (!monthlyData[monthKey]) {
          monthlyData[monthKey] = [];
        }
        monthlyData[monthKey].push(healthScore);
      } catch {
        // Skip invalid dates
        return;
      }
    });

    // Convert to array and calculate averages
    const history = Object.entries(monthlyData)
      .map(([monthKey, scores]) => {
        const [year, month] = monthKey.split('-');
        const date = new Date(parseInt(year), parseInt(month) - 1);
        const monthLabel = date.toLocaleDateString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh', month: 'short' });
        const avgScore = scores.reduce((sum, s) => sum + s, 0) / scores.length;
        // Round to 1 decimal place, then to integer for display
        const roundedScore = Math.round(Math.round(avgScore * 10) / 10);
        
        return {
          month: monthLabel,
          score: roundedScore,
          monthKey: monthKey // Keep for sorting
        };
      })
      .sort((a, b) => {
        // Sort by date - use monthKey for proper sorting
        const [yearA, monthA] = a.monthKey ? a.monthKey.split('-').map(Number) : [0, 0];
        const [yearB, monthB] = b.monthKey ? b.monthKey.split('-').map(Number) : [0, 0];
        if (yearA !== yearB) return yearA - yearB;
        return monthA - monthB;
      })
      .map(({ monthKey, ...rest }) => rest); // Remove monthKey from final result

    return history;
  };

  // Memoize health history when period changes
  const healthHistory = useMemo(() => {
    if (analysisResults.length === 0) return [];
    return calculateHealthHistory(analysisResults.filter(r => r.analysisStatus === 'Completed'), selectedPeriod);
  }, [analysisResults, selectedPeriod]);

  // Loading state
  if (loading) {
    return (
      <div className="bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50 font-sans antialiased min-h-screen flex flex-col transition-colors duration-200">
        <PatientHeader />
        <main className="flex-grow w-full max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="flex items-center justify-center min-h-[400px]">
            <div className="text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto mb-4"></div>
              <p className="text-slate-600 dark:text-slate-400">Đang tải dữ liệu dashboard...</p>
            </div>
          </div>
        </main>
      </div>
    );
  }

  // Use healthData or fallback to empty state
  const displayData = healthData || {
    healthScore: 0,
    lastAnalysisDate: null,
    status: 'stable' as const,
    riskAssessment: 'low' as const,
    cardiovascular: {
      status: 'good' as const,
      title: 'Chưa có dữ liệu',
      description: 'Chưa có phân tích nào được thực hiện.'
    },
    diabeticRetinopathy: {
      status: 'good' as const,
      title: 'Chưa có dữ liệu',
      description: 'Chưa có phân tích nào được thực hiện.'
    },
    stroke: {
      status: 'good' as const,
      title: 'Chưa có dữ liệu',
      description: 'Chưa có phân tích nào được thực hiện.'
    },
    healthHistory: [],
    recentReports: []
  };

  const hasNoData = !healthData || analysisResults.filter(r => r.analysisStatus === 'Completed').length === 0;

  const formatNoteDate = (dateString: string) => {
    return new Date(dateString).toLocaleDateString('vi-VN', {
      timeZone: 'Asia/Ho_Chi_Minh',
      day: 'numeric',
      month: 'short',
      year: 'numeric',
    });
  };

  return (
    <div className="bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-50 font-sans antialiased min-h-screen flex flex-col transition-colors duration-200">
      {/* Header */}
      <PatientHeader />

      {/* Main Content */}
      <main className="flex-grow w-full max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
        {/* Welcome Section */}
        <div className="flex flex-col md:flex-row md:items-end justify-between gap-4">
          <div className="flex flex-col gap-1">
            <h1 className="text-3xl font-black text-slate-900 dark:text-white tracking-tight">
              Chào mừng trở lại, {user?.firstName || 'Bạn'}
            </h1>
            <p className="text-slate-500 dark:text-slate-400 text-sm font-medium flex items-center gap-1">
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
              </svg>
              {displayData.lastAnalysisDate 
                ? `Lần phân tích gần nhất: ${displayData.lastAnalysisDate}`
                : 'Chưa có phân tích nào'}
            </p>
          </div>
        </div>

        {/* Main Grid */}
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Left Column - 2/3 width */}
          <div className="lg:col-span-2 space-y-6">
            {/* Health Score Card */}
            <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-100 dark:border-slate-800 overflow-hidden">
              <div className="p-6 flex flex-col md:flex-row gap-6">
                {/* Retinal Image */}
                <div className="relative w-full md:w-1/3 aspect-video md:aspect-square rounded-lg overflow-hidden bg-slate-900 group">
                  {latestImageUrl ? (
                    <>
                      <div 
                        className="absolute inset-0 bg-cover bg-center opacity-80" 
                        style={{ backgroundImage: `url("${latestImageUrl}")` }}
                      />
                      <div className="absolute inset-0 bg-gradient-to-t from-black/80 to-transparent"></div>
                      <div className="absolute bottom-3 left-3">
                        <span className="bg-blue-500/20 text-blue-100 border border-blue-500/30 text-xs px-2 py-1 rounded-full font-medium backdrop-blur-sm">
                          Đã phân tích AI
                        </span>
                      </div>
                    </>
                  ) : (
                    <div className="absolute inset-0 flex items-center justify-center bg-slate-800">
                      <div className="text-center text-slate-400">
                        <svg className="w-16 h-16 mx-auto mb-2" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z" />
                        </svg>
                        <p className="text-xs">Chưa có ảnh</p>
                      </div>
                    </div>
                  )}
                </div>

                {/* Health Info */}
                <div className="flex-1 flex flex-col justify-center">
                  {hasNoData ? (
                    <>
                      <div className="flex items-center gap-2 mb-2">
                        <span className="flex h-3 w-3 rounded-full bg-slate-400"></span>
                        <span className="text-sm font-bold text-slate-500 dark:text-slate-400 uppercase tracking-wide">
                          Chưa có dữ liệu
                        </span>
                      </div>
                      <h3 className="text-2xl md:text-3xl font-bold text-slate-900 dark:text-white mb-2">
                        Bắt đầu phân tích
                      </h3>
                      <p className="text-slate-500 dark:text-slate-400 text-base leading-relaxed mb-6">
                        Bạn chưa có phân tích nào. Hãy tải ảnh võng mạc lên để AI phân tích và đánh giá sức khỏe của bạn.
                      </p>
                    </>
                  ) : (
                    <>
                      <div className="flex items-center gap-2 mb-2">
                        <span className={`flex h-3 w-3 rounded-full ${
                          displayData.status === 'critical' ? 'bg-red-500' :
                          displayData.status === 'warning' ? 'bg-amber-500' :
                          'bg-emerald-500'
                        }`}></span>
                        <span className={`text-sm font-bold uppercase tracking-wide ${
                          displayData.status === 'critical' ? 'text-red-600 dark:text-red-400' :
                          displayData.status === 'warning' ? 'text-amber-600 dark:text-amber-400' :
                          'text-emerald-600 dark:text-emerald-400'
                        }`}>
                          {displayData.status === 'critical' ? 'Cần chú ý' :
                           displayData.status === 'warning' ? 'Cần theo dõi' :
                           'Tình trạng ổn định'}
                        </span>
                      </div>
                      <h3 className="text-2xl md:text-3xl font-bold text-slate-900 dark:text-white mb-2">
                        Đánh giá Rủi ro {displayData.riskAssessment === 'low' ? 'Thấp' :
                                         displayData.riskAssessment === 'medium' ? 'Trung bình' :
                                         displayData.riskAssessment === 'high' ? 'Cao' : 'Nghiêm trọng'}
                      </h3>
                      <p className="text-slate-500 dark:text-slate-400 text-base leading-relaxed mb-6">
                        {displayData.healthScore > 0 ? (
                          <>
                            Điểm sức khỏe võng mạc của bạn là <strong className="text-slate-900 dark:text-white">
                              {displayData.healthScore % 1 === 0 
                                ? displayData.healthScore.toFixed(0) 
                                : displayData.healthScore.toFixed(1)}/100
                            </strong>.
                            {displayData.riskAssessment === 'low' 
                              ? ' Không phát hiện rủi ro hệ thống ngay lập tức. Tiếp tục theo dõi định kỳ.'
                              : displayData.riskAssessment === 'medium'
                              ? ' Có một số dấu hiệu cần theo dõi. Nên tư vấn bác sĩ.'
                              : ' Có dấu hiệu rủi ro cao. Nên tư vấn bác sĩ ngay.'}
                          </>
                        ) : (
                          'Chưa có đủ dữ liệu để đánh giá. Hãy thực hiện phân tích để có kết quả chính xác.'
                        )}
                      </p>
                      
                      {/* Score Bar */}
                      {displayData.healthScore > 0 && (
                        <div className="space-y-2">
                          <div className="flex justify-between text-xs font-semibold uppercase text-slate-400 tracking-wider">
                            <span>Điểm sức khỏe</span>
                            <span>
                              {displayData.healthScore % 1 === 0 
                                ? displayData.healthScore.toFixed(0) 
                                : displayData.healthScore.toFixed(1)}%
                            </span>
                          </div>
                          <div className="h-3 w-full bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                            <div 
                              className={`h-full rounded-full transition-all duration-500 ${
                                displayData.healthScore >= 70
                                  ? 'bg-gradient-to-r from-emerald-400 to-emerald-600 shadow-[0_0_10px_rgba(16,185,129,0.3)]'
                                  : displayData.healthScore >= 40
                                  ? 'bg-gradient-to-r from-amber-400 to-amber-600 shadow-[0_0_10px_rgba(245,158,11,0.3)]'
                                  : 'bg-gradient-to-r from-red-400 to-red-600 shadow-[0_0_10px_rgba(239,68,68,0.3)]'
                              }`}
                              style={{ width: `${displayData.healthScore}%` }}
                            />
                          </div>
                        </div>
                      )}
                    </>
                  )}
                  
                  <div className="mt-6 flex gap-3">
                    <button 
                      onClick={() => navigate('/upload')}
                      className="bg-blue-500 hover:bg-blue-600 text-white px-5 py-2.5 rounded-lg text-sm font-bold shadow-md shadow-blue-500/20 transition-all active:scale-95 flex items-center gap-2"
                    >
                      Tải ảnh mới để phân tích
                      <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
                      </svg>
                    </button>
                  </div>
                </div>
              </div>
            </div>

            {/* Risk Indicator Cards */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              {/* Cardiovascular */}
              <div className={`bg-white dark:bg-slate-900 p-5 rounded-xl border shadow-sm flex flex-col gap-3 transition-colors group ${
                displayData.cardiovascular.status === 'critical' 
                  ? 'border-red-200 dark:border-red-800 hover:border-red-500/30' 
                  : 'border-slate-100 dark:border-slate-800 hover:border-blue-500/30'
              }`}>
                <div className="flex items-start justify-between">
                  <div className={`p-2 rounded-lg group-hover:scale-110 transition-transform ${
                    displayData.cardiovascular.status === 'critical'
                      ? 'bg-red-50 dark:bg-red-900/20 text-red-500'
                      : 'bg-rose-50 dark:bg-rose-900/20 text-rose-500 dark:text-rose-400'
                  }`}>
                    <svg className="w-6 h-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4.318 6.318a4.5 4.5 0 000 6.364L12 20.364l7.682-7.682a4.5 4.5 0 00-6.364-6.364L12 7.636l-1.318-1.318a4.5 4.5 0 00-6.364 0z" />
                    </svg>
                  </div>
                  <svg className={`w-6 h-6 ${
                    displayData.cardiovascular.status === 'critical' ? 'text-red-500' :
                    displayData.cardiovascular.status === 'warning' ? 'text-amber-500' :
                    'text-emerald-500'
                  }`} fill="currentColor" viewBox="0 0 24 24">
                    {displayData.cardiovascular.status === 'critical' || displayData.cardiovascular.status === 'warning' ? (
                      <path fillRule="evenodd" d="M9.401 3.003c1.155-2 4.043-2 5.197 0l7.355 12.748c1.154 2-.29 4.5-2.599 4.5H4.645c-2.309 0-3.752-2.5-2.598-4.5L9.4 3.003zM12 8.25a.75.75 0 01.75.75v3.75a.75.75 0 01-1.5 0V9a.75.75 0 01.75-.75zm0 8.25a.75.75 0 100-1.5.75.75 0 000 1.5z" clipRule="evenodd" />
                    ) : (
                      <path fillRule="evenodd" d="M2.25 12c0-5.385 4.365-9.75 9.75-9.75s9.75 4.365 9.75 9.75-4.365 9.75-9.75 9.75S2.25 17.385 2.25 12zm13.36-1.814a.75.75 0 10-1.22-.872l-3.236 4.53L9.53 12.22a.75.75 0 00-1.06 1.06l2.25 2.25a.75.75 0 001.14-.094l3.75-5.25z" clipRule="evenodd" />
                    )}
                  </svg>
                </div>
                <div>
                  <p className="text-xs font-medium text-slate-400 uppercase tracking-wider mb-1">Tim mạch</p>
                  <h4 className="text-slate-900 dark:text-white font-bold text-lg">{displayData.cardiovascular.title}</h4>
                  <p className="text-slate-500 dark:text-slate-400 text-sm mt-1">{displayData.cardiovascular.description}</p>
                </div>
              </div>

              {/* Diabetic Retinopathy */}
              <div className={`bg-white dark:bg-slate-900 p-5 rounded-xl border shadow-sm flex flex-col gap-3 transition-colors group ${
                displayData.diabeticRetinopathy.status === 'critical' 
                  ? 'border-red-200 dark:border-red-800 hover:border-red-500/30' 
                  : 'border-slate-100 dark:border-slate-800 hover:border-blue-500/30'
              }`}>
                <div className="flex items-start justify-between">
                  <div className={`p-2 rounded-lg group-hover:scale-110 transition-transform ${
                    displayData.diabeticRetinopathy.status === 'critical'
                      ? 'bg-red-50 dark:bg-red-900/20 text-red-500'
                      : 'bg-blue-50 dark:bg-blue-900/20 text-blue-500'
                  }`}>
                    <svg className="w-6 h-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
                    </svg>
                  </div>
                  <svg className={`w-6 h-6 ${
                    displayData.diabeticRetinopathy.status === 'critical' ? 'text-red-500' :
                    displayData.diabeticRetinopathy.status === 'warning' ? 'text-amber-500' :
                    'text-emerald-500'
                  }`} fill="currentColor" viewBox="0 0 24 24">
                    {displayData.diabeticRetinopathy.status === 'critical' || displayData.diabeticRetinopathy.status === 'warning' ? (
                      <path fillRule="evenodd" d="M9.401 3.003c1.155-2 4.043-2 5.197 0l7.355 12.748c1.154 2-.29 4.5-2.599 4.5H4.645c-2.309 0-3.752-2.5-2.598-4.5L9.4 3.003zM12 8.25a.75.75 0 01.75.75v3.75a.75.75 0 01-1.5 0V9a.75.75 0 01.75-.75zm0 8.25a.75.75 0 100-1.5.75.75 0 000 1.5z" clipRule="evenodd" />
                    ) : (
                      <path fillRule="evenodd" d="M2.25 12c0-5.385 4.365-9.75 9.75-9.75s9.75 4.365 9.75 9.75-4.365 9.75-9.75 9.75S2.25 17.385 2.25 12zm13.36-1.814a.75.75 0 10-1.22-.872l-3.236 4.53L9.53 12.22a.75.75 0 00-1.06 1.06l2.25 2.25a.75.75 0 001.14-.094l3.75-5.25z" clipRule="evenodd" />
                    )}
                  </svg>
                </div>
                <div>
                  <p className="text-xs font-medium text-slate-400 uppercase tracking-wider mb-1">Võng mạc đái tháo đường</p>
                  <h4 className="text-slate-900 dark:text-white font-bold text-lg">{displayData.diabeticRetinopathy.title}</h4>
                  <p className="text-slate-500 dark:text-slate-400 text-sm mt-1">{displayData.diabeticRetinopathy.description}</p>
                </div>
              </div>

              {/* Stroke */}
              <div className={`bg-white dark:bg-slate-900 p-5 rounded-xl border shadow-sm flex flex-col gap-3 transition-colors group ${
                displayData.stroke.status === 'critical' 
                  ? 'border-red-200 dark:border-red-800 hover:border-red-500/30' 
                  : 'border-slate-100 dark:border-slate-800 hover:border-amber-500/30'
              }`}>
                <div className="flex items-start justify-between">
                  <div className={`p-2 rounded-lg group-hover:scale-110 transition-transform ${
                    displayData.stroke.status === 'critical'
                      ? 'bg-red-50 dark:bg-red-900/20 text-red-500'
                      : 'bg-amber-50 dark:bg-amber-900/20 text-amber-500'
                  }`}>
                    <svg className="w-6 h-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                    </svg>
                  </div>
                  <svg className={`w-6 h-6 ${
                    displayData.stroke.status === 'critical' ? 'text-red-500' :
                    displayData.stroke.status === 'warning' ? 'text-amber-500' :
                    'text-emerald-500'
                  }`} fill="currentColor" viewBox="0 0 24 24">
                    {displayData.stroke.status === 'critical' || displayData.stroke.status === 'warning' ? (
                      <path fillRule="evenodd" d="M9.401 3.003c1.155-2 4.043-2 5.197 0l7.355 12.748c1.154 2-.29 4.5-2.599 4.5H4.645c-2.309 0-3.752-2.5-2.598-4.5L9.4 3.003zM12 8.25a.75.75 0 01.75.75v3.75a.75.75 0 01-1.5 0V9a.75.75 0 01.75-.75zm0 8.25a.75.75 0 100-1.5.75.75 0 000 1.5z" clipRule="evenodd" />
                    ) : (
                      <path fillRule="evenodd" d="M2.25 12c0-5.385 4.365-9.75 9.75-9.75s9.75 4.365 9.75 9.75-4.365 9.75-9.75 9.75S2.25 17.385 2.25 12zm13.36-1.814a.75.75 0 10-1.22-.872l-3.236 4.53L9.53 12.22a.75.75 0 00-1.06 1.06l2.25 2.25a.75.75 0 001.14-.094l3.75-5.25z" clipRule="evenodd" />
                    )}
                  </svg>
                </div>
                <div>
                  <p className="text-xs font-medium text-slate-400 uppercase tracking-wider mb-1">Đột quỵ</p>
                  <h4 className="text-slate-900 dark:text-white font-bold text-lg">{displayData.stroke.title}</h4>
                  <p className="text-slate-500 dark:text-slate-400 text-sm mt-1">{displayData.stroke.description}</p>
                </div>
              </div>
            </div>

            {/* Health Score History Chart */}
            <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-100 dark:border-slate-800 shadow-sm p-6">
              <div className="flex items-center justify-between mb-6">
                <div>
                  <h3 className="text-lg font-bold text-slate-900 dark:text-white">Lịch sử Điểm Sức khỏe</h3>
                  <p className="text-sm text-slate-500 dark:text-slate-400">Theo dõi điểm sức khỏe võng mạc của bạn trong 6 tháng qua</p>
                </div>
                <select 
                  value={selectedPeriod}
                  onChange={(e) => setSelectedPeriod(e.target.value)}
                  className="bg-slate-50 dark:bg-slate-800 border-none text-sm rounded-lg py-1 px-3 text-slate-600 dark:text-slate-300 focus:ring-1 focus:ring-blue-500 cursor-pointer"
                >
                  <option value="6months">6 tháng qua</option>
                  <option value="1year">Năm qua</option>
                </select>
              </div>
              
              {/* Chart */}
              {healthHistory.length > 0 ? (
                <div className="h-48 w-full flex items-end justify-between gap-2 md:gap-4 px-2">
                  {healthHistory.map((item, index) => {
                    const isLatest = index === healthHistory.length - 1;
                    const heightPercent = (item.score / 100) * 100;
                    
                    return (
                      <div key={`${item.month}-${index}`} className="group relative flex-1 flex flex-col items-center">
                        <div 
                          className={`w-full rounded-t-sm transition-all cursor-pointer ${
                            isLatest 
                              ? 'bg-blue-500/80 hover:bg-blue-500 shadow-[0_0_15px_rgba(43,140,238,0.4)]' 
                              : 'bg-slate-100 dark:bg-slate-800 hover:bg-blue-200 dark:hover:bg-blue-900/50'
                          }`}
                          style={{ height: `${heightPercent}%` }}
                        >
                          <div className={`absolute -top-8 left-1/2 -translate-x-1/2 text-white text-xs py-1 px-2 rounded opacity-0 group-hover:opacity-100 transition-opacity ${
                            isLatest ? 'bg-blue-500 font-bold' : 'bg-slate-800'
                          }`}>
                            {item.score}
                          </div>
                        </div>
                        <div className={`absolute -bottom-6 left-1/2 -translate-x-1/2 text-xs font-medium ${
                          isLatest ? 'text-blue-500 font-bold' : 'text-slate-400'
                        }`}>
                          {item.month}
                        </div>
                      </div>
                    );
                  })}
                </div>
              ) : (
                <div className="h-48 w-full flex items-center justify-center text-slate-400 dark:text-slate-500">
                  <div className="text-center">
                    <svg className="w-12 h-12 mx-auto mb-2 opacity-50" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                    </svg>
                    <p className="text-sm">Chưa có dữ liệu lịch sử</p>
                    <p className="text-xs mt-1">Thực hiện phân tích để xem biểu đồ</p>
                  </div>
                </div>
              )}
              <div className="h-6 w-full"></div>
            </div>
          </div>

          {/* Right Column - 1/3 width */}
          <div className="space-y-6">
            {/* Upload New Scan */}
            <div className="bg-gradient-to-br from-blue-500/10 to-transparent dark:from-blue-500/20 dark:to-slate-900 rounded-xl p-6 border border-blue-500/20 flex flex-col items-center text-center gap-4 relative overflow-hidden group">
              <div className="absolute top-0 right-0 w-24 h-24 bg-blue-500/10 rounded-bl-full -mr-4 -mt-4 transition-transform group-hover:scale-110"></div>
              <div className="bg-white dark:bg-slate-800 p-3 rounded-full shadow-sm text-blue-500 z-10">
                <svg className="w-8 h-8" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z" />
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 13a3 3 0 11-6 0 3 3 0 016 0z" />
                </svg>
              </div>
              <div className="z-10">
                <h3 className="text-lg font-bold text-slate-900 dark:text-white">Cần quét hình ảnh mới?</h3>
                <p className="text-sm text-slate-600 dark:text-slate-300 mt-1">Tải lên hình ảnh đáy mắt mới để AI phân tích ngay lập tức.</p>
              </div>
              <button 
                onClick={() => navigate('/upload')}
                className="w-full bg-blue-500 hover:bg-blue-600 text-white py-3 px-4 rounded-lg font-bold text-sm shadow-md transition-colors z-10 flex items-center justify-center gap-2"
              >
                <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
                </svg>
                Tải lên Ảnh Đáy mắt
              </button>
              <div className="flex items-center gap-1.5 text-xs text-slate-400 dark:text-slate-500 z-10 mt-2">
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
                </svg>
                <span>Bảo mật & Tuân thủ Y tế</span>
              </div>
            </div>

            {/* Recent Reports */}
            <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-100 dark:border-slate-800 shadow-sm flex flex-col h-full max-h-[400px]">
              <div className="p-4 border-b border-slate-100 dark:border-slate-800 flex justify-between items-center bg-slate-50/50 dark:bg-slate-800/50 rounded-t-xl">
                <h3 className="font-bold text-slate-900 dark:text-white">Báo cáo gần đây</h3>
                <Link to="/reports" className="text-xs font-semibold text-blue-500 hover:underline">Xem tất cả</Link>
              </div>
              <div className="overflow-y-auto flex-1 p-2">
                {displayData.recentReports.length > 0 ? (
                  <div className="space-y-1">
                    {displayData.recentReports.map((report) => (
                      <Link
                        key={report.id}
                        to={`/analysis/${report.id}`}
                        className="flex items-center gap-3 p-3 hover:bg-slate-50 dark:hover:bg-slate-800/50 rounded-lg transition-colors group cursor-pointer"
                      >
                        <div className={`h-10 w-10 rounded-lg flex items-center justify-center flex-shrink-0 ${
                          report.risk === 'low' 
                            ? 'bg-emerald-100 dark:bg-emerald-900/30 text-emerald-600 dark:text-emerald-400'
                            : report.risk === 'medium'
                            ? 'bg-amber-100 dark:bg-amber-900/30 text-amber-600 dark:text-amber-400'
                            : 'bg-red-100 dark:bg-red-900/30 text-red-600 dark:text-red-400'
                        }`}>
                          {report.risk === 'low' ? (
                            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                            </svg>
                          ) : (
                            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                            </svg>
                          )}
                        </div>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-semibold text-slate-900 dark:text-white truncate">{report.title}</p>
                          <p className="text-xs text-slate-500 dark:text-slate-400">
                            {report.date} • {report.risk === 'low' ? 'Rủi ro thấp' : report.risk === 'medium' ? 'Rủi ro trung bình' : report.risk === 'high' ? 'Rủi ro cao' : 'Rủi ro nghiêm trọng'}
                          </p>
                        </div>
                      </Link>
                    ))}
                  </div>
                ) : (
                  <div className="flex items-center justify-center h-32 text-slate-400 dark:text-slate-500">
                    <div className="text-center">
                      <svg className="w-10 h-10 mx-auto mb-2 opacity-50" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                      </svg>
                      <p className="text-sm">Chưa có báo cáo</p>
                      <p className="text-xs mt-1">Thực hiện phân tích để xem báo cáo</p>
                    </div>
                  </div>
                )}
              </div>
            </div>

            {/* Medical Notes from Doctor */}
            <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-100 dark:border-slate-800 shadow-sm flex flex-col max-h-[350px]">
              <div className="p-4 border-b border-slate-100 dark:border-slate-800 flex justify-between items-center bg-gradient-to-r from-green-50 to-transparent dark:from-green-900/20 dark:to-transparent rounded-t-xl">
                <div className="flex items-center gap-2">
                  <div className="p-1.5 bg-green-100 dark:bg-green-900/30 rounded-lg">
                    <svg className="w-4 h-4 text-green-600 dark:text-green-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                    </svg>
                  </div>
                  <h3 className="font-bold text-slate-900 dark:text-white">Ghi chú từ bác sĩ</h3>
                </div>
                <Link to="/notes" className="text-xs font-semibold text-green-600 hover:underline">Xem tất cả</Link>
              </div>
              <div className="overflow-y-auto flex-1 p-2">
                {loadingNotes ? (
                  <div className="p-4 text-center">
                    <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-green-600 mx-auto"></div>
                  </div>
                ) : recentNotes.length === 0 ? (
                  <div className="p-6 text-center">
                    <svg className="w-10 h-10 mx-auto text-slate-300 dark:text-slate-600 mb-2" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                    </svg>
                    <p className="text-sm text-slate-500 dark:text-slate-400">Chưa có ghi chú nào</p>
                    <p className="text-xs text-slate-400 dark:text-slate-500 mt-1">Bác sĩ sẽ ghi chú sau khi khám</p>
                  </div>
                ) : (
                  <div className="space-y-1">
                    {recentNotes.map((note) => (
                      <Link 
                        key={note.id} 
                        to="/notes"
                        className="flex items-start gap-3 p-3 hover:bg-slate-50 dark:hover:bg-slate-800/50 rounded-lg transition-colors group cursor-pointer"
                      >
                        <div className={`h-9 w-9 rounded-lg flex items-center justify-center flex-shrink-0 ${
                          note.severity?.toLowerCase() === 'high' || note.severity?.toLowerCase() === 'critical'
                            ? 'bg-red-100 dark:bg-red-900/30 text-red-600 dark:text-red-400'
                            : note.severity?.toLowerCase() === 'medium'
                            ? 'bg-amber-100 dark:bg-amber-900/30 text-amber-600 dark:text-amber-400'
                            : 'bg-green-100 dark:bg-green-900/30 text-green-600 dark:text-green-400'
                        }`}>
                          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                          </svg>
                        </div>
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center gap-2">
                            <p className="text-sm font-semibold text-slate-900 dark:text-white truncate">
                              BS. {note.doctorName || 'Bác sĩ'}
                            </p>
                            <span className={`text-[10px] px-1.5 py-0.5 rounded font-medium ${
                              note.noteType.toLowerCase() === 'diagnosis' 
                                ? 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400'
                                : note.noteType.toLowerCase() === 'followup'
                                ? 'bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-400'
                                : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400'
                            }`}>
                              {medicalNotesService.formatNoteType(note.noteType)}
                            </span>
                          </div>
                          <p className="text-xs text-slate-600 dark:text-slate-400 truncate mt-0.5">
                            {note.noteContent.substring(0, 50)}{note.noteContent.length > 50 ? '...' : ''}
                          </p>
                          <p className="text-[10px] text-slate-400 dark:text-slate-500 mt-1">
                            {formatNoteDate(note.createdAt)}
                          </p>
                        </div>
                        <svg className="w-4 h-4 text-slate-300 group-hover:text-green-500 transition-colors flex-shrink-0 mt-1" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                        </svg>
                      </Link>
                    ))}
                  </div>
                )}
              </div>
            </div>

          </div>
        </div>
      </main>

      {/* Footer */}
      <footer className="mt-auto py-8 text-center px-4 border-t border-slate-200 dark:border-slate-800">
        <div className="max-w-3xl mx-auto space-y-4">
          <p className="text-xs text-slate-400 dark:text-slate-500 leading-relaxed">
            <span className="font-bold">Tuyên bố miễn trừ trách nhiệm y tế:</span> AURA AI là một công cụ sàng lọc được thiết kế để hỗ trợ các chuyên gia y tế. 
            Nó không phải là công cụ chẩn đoán và không thay thế lời khuyên của bác sĩ có chuyên môn. 
            Nếu bạn đang gặp phải những thay đổi về thị lực hoặc đau mắt, vui lòng tìm kiếm sự chăm sóc y tế ngay lập tức.
          </p>
          <div className="flex justify-center gap-6 text-xs text-slate-400">
            <Link to="/privacy" className="hover:text-slate-600 dark:hover:text-slate-300 transition-colors">Chính sách bảo mật</Link>
            <Link to="/terms" className="hover:text-slate-600 dark:hover:text-slate-300 transition-colors">Điều khoản dịch vụ</Link>
            <Link to="/support" className="hover:text-slate-600 dark:hover:text-slate-300 transition-colors">Hỗ trợ</Link>
          </div>
          <p className="text-xs text-slate-300 dark:text-slate-600">© 2026 AURA AI Inc.</p>
        </div>
      </footer>

      {/* Floating Chat Button */}
      <Link
        to="/chat"
        className="fixed bottom-6 right-6 z-50 flex items-center justify-center w-14 h-14 bg-primary text-white rounded-full shadow-lg shadow-primary/30 hover:bg-primary-dark transition-all hover:scale-110 group"
        title="Chat tư vấn với bác sĩ"
      >
        <span className="material-symbols-outlined text-2xl">chat</span>
        {unreadCount > 0 && (
          <span className="absolute -top-1 -right-1 bg-red-500 text-white text-xs font-bold rounded-full w-6 h-6 flex items-center justify-center ring-2 ring-white dark:ring-slate-900 animate-pulse">
            {unreadCount > 9 ? "9+" : unreadCount}
          </span>
        )}
      </Link>
    </div>
  );
};

export default PatientDashboard;
