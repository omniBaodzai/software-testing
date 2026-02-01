import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import ClinicHeader from '../../components/clinic/ClinicHeader';
import clinicAuthService from '../../services/clinicAuthService';
import clinicPackageService, { ServicePackage, CurrentPackage, PaymentHistory } from '../../services/clinicPackageService';
import toast from 'react-hot-toast';

const ClinicPackagesPage = () => {
  const navigate = useNavigate();
  const [loading, setLoading] = useState(true);
  const [packages, setPackages] = useState<ServicePackage[]>([]);
  const [currentPackage, setCurrentPackage] = useState<CurrentPackage | null>(null);
  const [history, setHistory] = useState<PaymentHistory[]>([]);
  const [showPurchaseModal, setShowPurchaseModal] = useState<ServicePackage | null>(null);
  const [purchasing, setPurchasing] = useState(false);
  const [activeTab, setActiveTab] = useState<'packages' | 'history'>('packages');

  useEffect(() => {
    if (!clinicAuthService.isLoggedIn()) {
      navigate('/login');
      return;
    }
    fetchData();
  }, [navigate]);

  const fetchData = async () => {
    try {
      setLoading(true);
      const [packagesData, currentData, historyData] = await Promise.all([
        clinicPackageService.getAvailablePackages(),
        clinicPackageService.getCurrentPackage(),
        clinicPackageService.getPurchaseHistory(),
      ]);
      setPackages(packagesData);
      setCurrentPackage(currentData);
      setHistory(historyData);
    } catch (error: any) {
      console.error('Error fetching data:', error);
      if (error.response?.status === 401) {
        navigate('/login');
      }
    } finally {
      setLoading(false);
    }
  };

  const handlePurchase = async (pkg: ServicePackage) => {
    setPurchasing(true);
    try {
      const result = await clinicPackageService.purchasePackage(pkg.id, 'BankTransfer');
      if (result.success) {
        toast.success(result.message);
        setShowPurchaseModal(null);
        fetchData();
      } else {
        toast.error(result.message || 'Không thể mua gói dịch vụ');
      }
    } catch (error: any) {
      const message = error.response?.data?.message || 'Đã xảy ra lỗi';
      toast.error(message);
    } finally {
      setPurchasing(false);
    }
  };

  const formatPrice = (price: number) => {
    return new Intl.NumberFormat('vi-VN', {
      style: 'currency',
      currency: 'VND',
    }).format(price);
  };

  const formatDate = (dateStr: string) => {
    return new Date(dateStr).toLocaleDateString('vi-VN', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
    });
  };

  return (
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950">
      <ClinicHeader />
      
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {/* Header */}
        <div className="mb-8">
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Gói dịch vụ</h1>
          <p className="text-slate-600 dark:text-slate-400 mt-1">
            Quản lý và mua gói phân tích cho phòng khám
          </p>
        </div>

        {loading ? (
          <div className="flex items-center justify-center py-20">
            <div className="w-8 h-8 border-4 border-indigo-500 border-t-transparent rounded-full animate-spin"></div>
          </div>
        ) : (
          <>
            {/* Current Package */}
            {currentPackage && (
              <div className="bg-gradient-to-r from-indigo-500 to-purple-600 rounded-2xl p-6 text-white mb-8">
                <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                  <div>
                    <p className="text-indigo-100 text-sm mb-1">Gói hiện tại</p>
                    <h2 className="text-2xl font-bold">{currentPackage.packageName}</h2>
                  </div>
                  <div className="flex flex-col items-end">
                    <div className="text-4xl font-bold">{currentPackage.remainingAnalyses}</div>
                    <p className="text-indigo-100">lượt phân tích còn lại</p>
                    <div className="mt-2 w-48 bg-indigo-400/30 rounded-full h-2">
                      <div
                        className="bg-white h-2 rounded-full"
                        style={{
                          width: `${Math.min(100, (currentPackage.remainingAnalyses / currentPackage.totalAnalyses) * 100)}%`,
                        }}
                      ></div>
                    </div>
                    <p className="text-xs text-indigo-200 mt-1">
                      Đã dùng {currentPackage.usedAnalyses} / {currentPackage.totalAnalyses}
                    </p>
                  </div>
                </div>
              </div>
            )}

            {/* Tabs */}
            <div className="flex gap-2 mb-6">
              <button
                onClick={() => setActiveTab('packages')}
                className={`px-4 py-2 rounded-lg font-medium transition-colors ${
                  activeTab === 'packages'
                    ? 'bg-indigo-600 text-white'
                    : 'bg-white dark:bg-slate-900 text-slate-600 dark:text-slate-400 border border-slate-200 dark:border-slate-700 hover:bg-slate-100 dark:hover:bg-slate-800'
                }`}
              >
                Các gói dịch vụ
              </button>
              <button
                onClick={() => setActiveTab('history')}
                className={`px-4 py-2 rounded-lg font-medium transition-colors ${
                  activeTab === 'history'
                    ? 'bg-indigo-600 text-white'
                    : 'bg-white dark:bg-slate-900 text-slate-600 dark:text-slate-400 border border-slate-200 dark:border-slate-700 hover:bg-slate-100 dark:hover:bg-slate-800'
                }`}
              >
                Lịch sử mua hàng
              </button>
            </div>

            {activeTab === 'packages' ? (
              /* Packages Grid */
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                {packages.map((pkg) => (
                  <div
                    key={pkg.id}
                    className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 overflow-hidden hover:shadow-md transition-shadow"
                  >
                    <div className="p-6">
                      <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
                        {pkg.packageName}
                      </h3>
                      {pkg.description && (
                        <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                          {pkg.description}
                        </p>
                      )}
                      <div className="mt-4">
                        <span className="text-3xl font-bold text-slate-900 dark:text-white">
                          {formatPrice(pkg.price)}
                        </span>
                      </div>
                      <ul className="mt-4 space-y-2">
                        <li className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-400">
                          <svg className="w-5 h-5 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                          {pkg.analysesIncluded.toLocaleString()} lượt phân tích
                        </li>
                        <li className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-400">
                          <svg className="w-5 h-5 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                          Hiệu lực {pkg.validityDays} ngày
                        </li>
                        {pkg.features && pkg.features.split(',').map((feature, idx) => (
                          <li key={idx} className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-400">
                            <svg className="w-5 h-5 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                            </svg>
                            {feature.trim()}
                          </li>
                        ))}
                      </ul>
                    </div>
                    <div className="px-6 pb-6">
                      <button
                        onClick={() => setShowPurchaseModal(pkg)}
                        className="w-full py-3 bg-indigo-600 hover:bg-indigo-700 text-white font-medium rounded-lg transition-colors"
                      >
                        Mua ngay
                      </button>
                    </div>
                  </div>
                ))}

                {packages.length === 0 && (
                  <div className="col-span-full text-center py-12 text-slate-500 dark:text-slate-400">
                    Chưa có gói dịch vụ nào
                  </div>
                )}
              </div>
            ) : (
              /* Purchase History */
              <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 overflow-hidden">
                {history.length === 0 ? (
                  <div className="p-12 text-center text-slate-500 dark:text-slate-400">
                    <svg className="w-16 h-16 mx-auto mb-4 text-slate-300 dark:text-slate-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                    </svg>
                    <p>Chưa có lịch sử mua hàng</p>
                  </div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="w-full">
                      <thead className="bg-slate-50 dark:bg-slate-800/50">
                        <tr>
                          <th className="text-left px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase">Mã giao dịch</th>
                          <th className="text-left px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase">Gói dịch vụ</th>
                          <th className="text-right px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase">Số tiền</th>
                          <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase">Trạng thái</th>
                          <th className="text-right px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase">Ngày mua</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-200 dark:divide-slate-700">
                        {history.map((item) => (
                          <tr key={item.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/50">
                            <td className="px-6 py-4">
                              <span className="font-mono text-sm text-slate-600 dark:text-slate-400">
                                {item.transactionId || '-'}
                              </span>
                            </td>
                            <td className="px-6 py-4">
                              <div>
                                <p className="font-medium text-slate-900 dark:text-white">{item.packageName}</p>
                                <p className="text-sm text-slate-500 dark:text-slate-400">
                                  {item.analysesIncluded.toLocaleString()} lượt phân tích
                                </p>
                              </div>
                            </td>
                            <td className="px-6 py-4 text-right font-medium text-slate-900 dark:text-white">
                              {formatPrice(item.amount)}
                            </td>
                            <td className="px-6 py-4 text-center">
                              <span className={`inline-flex px-2 py-1 text-xs font-medium rounded-full ${
                                item.paymentStatus === 'Completed'
                                  ? 'bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-400'
                                  : item.paymentStatus === 'Pending'
                                    ? 'bg-yellow-100 dark:bg-yellow-900/30 text-yellow-700 dark:text-yellow-400'
                                    : 'bg-red-100 dark:bg-red-900/30 text-red-700 dark:text-red-400'
                              }`}>
                                {item.paymentStatus === 'Completed' ? 'Hoàn thành' :
                                 item.paymentStatus === 'Pending' ? 'Đang xử lý' : 'Thất bại'}
                              </span>
                            </td>
                            <td className="px-6 py-4 text-right text-sm text-slate-600 dark:text-slate-400">
                              {item.paidAt ? formatDate(item.paidAt) : '-'}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </main>

      {/* Purchase Modal */}
      {showPurchaseModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl w-full max-w-md p-6">
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">
              Xác nhận mua gói
            </h3>
            <div className="bg-slate-50 dark:bg-slate-800 rounded-xl p-4 mb-6">
              <h4 className="font-semibold text-slate-900 dark:text-white">{showPurchaseModal.packageName}</h4>
              <p className="text-2xl font-bold text-indigo-600 dark:text-indigo-400 mt-2">
                {formatPrice(showPurchaseModal.price)}
              </p>
              <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                {showPurchaseModal.analysesIncluded.toLocaleString()} lượt phân tích • Hiệu lực {showPurchaseModal.validityDays} ngày
              </p>
            </div>
            
            <div className="bg-yellow-50 dark:bg-yellow-900/20 rounded-xl p-4 mb-6">
              <p className="text-sm text-yellow-700 dark:text-yellow-400">
                Đây là bản demo. Trong sản phẩm thực tế, bạn sẽ được chuyển đến cổng thanh toán để hoàn tất giao dịch.
              </p>
            </div>

            <div className="flex gap-3">
              <button
                onClick={() => setShowPurchaseModal(null)}
                className="flex-1 px-4 py-2 border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800"
                disabled={purchasing}
              >
                Hủy
              </button>
              <button
                onClick={() => handlePurchase(showPurchaseModal)}
                disabled={purchasing}
                className="flex-1 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg disabled:opacity-50 flex items-center justify-center gap-2"
              >
                {purchasing ? (
                  <>
                    <div className="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
                    Đang xử lý...
                  </>
                ) : (
                  'Xác nhận mua'
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default ClinicPackagesPage;
