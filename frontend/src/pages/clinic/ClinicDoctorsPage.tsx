import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import ClinicHeader from '../../components/clinic/ClinicHeader';
import clinicAuthService from '../../services/clinicAuthService';
import clinicManagementService, { ClinicDoctor, AddDoctorData } from '../../services/clinicManagementService';
import toast from 'react-hot-toast';

const ClinicDoctorsPage = () => {
  const navigate = useNavigate();
  const [loading, setLoading] = useState(true);
  const [doctors, setDoctors] = useState<ClinicDoctor[]>([]);
  const [search, setSearch] = useState('');
  const [showAddModal, setShowAddModal] = useState(false);
  const [showDeleteModal, setShowDeleteModal] = useState<string | null>(null);
  const [addLoading, setAddLoading] = useState(false);
  const [newDoctor, setNewDoctor] = useState<AddDoctorData>({
    email: '',
    fullName: '',
    phone: '',
    specialization: '',
    isPrimary: false,
    password: '',
  });

  useEffect(() => {
    if (!clinicAuthService.isLoggedIn()) {
      navigate('/login');
      return;
    }
    fetchDoctors();
  }, [navigate]);

  const fetchDoctors = async () => {
    try {
      setLoading(true);
      const data = await clinicManagementService.getDoctors(search || undefined);
      setDoctors(data);
    } catch (error: any) {
      console.error('Error fetching doctors:', error);
      if (error.response?.status === 401) {
        navigate('/login');
      }
    } finally {
      setLoading(false);
    }
  };

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    fetchDoctors();
  };

  const handleAddDoctor = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newDoctor.email || !newDoctor.fullName) {
      toast.error('Vui lòng nhập đầy đủ thông tin');
      return;
    }

    setAddLoading(true);
    try {
      await clinicManagementService.addDoctor(newDoctor);
      toast.success('Thêm bác sĩ thành công!');
      setShowAddModal(false);
      setNewDoctor({ email: '', fullName: '', phone: '', specialization: '', isPrimary: false, password: '' });
      fetchDoctors();
    } catch (error: any) {
      const message = error.response?.data?.message || 'Không thể thêm bác sĩ';
      toast.error(message);
    } finally {
      setAddLoading(false);
    }
  };

  const handleSetPrimary = async (doctorId: string) => {
    try {
      await clinicManagementService.setPrimaryDoctor(doctorId);
      toast.success('Đã đặt làm bác sĩ chính');
      fetchDoctors();
    } catch (error) {
      toast.error('Không thể cập nhật');
    }
  };

  const handleToggleActive = async (doctorId: string, isActive: boolean) => {
    try {
      await clinicManagementService.updateDoctor(doctorId, { isActive: !isActive });
      toast.success(isActive ? 'Đã vô hiệu hóa bác sĩ' : 'Đã kích hoạt bác sĩ');
      fetchDoctors();
    } catch (error) {
      toast.error('Không thể cập nhật');
    }
  };

  const handleRemoveDoctor = async (doctorId: string) => {
    try {
      await clinicManagementService.removeDoctor(doctorId);
      toast.success('Đã xóa bác sĩ khỏi phòng khám');
      setShowDeleteModal(null);
      fetchDoctors();
    } catch (error) {
      toast.error('Không thể xóa bác sĩ');
    }
  };

  return (
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950">
      <ClinicHeader />
      
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {/* Header */}
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
          <div>
            <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Quản lý bác sĩ</h1>
            <p className="text-slate-600 dark:text-slate-400 mt-1">
              {doctors.length} bác sĩ trong phòng khám
            </p>
          </div>
          <button
            onClick={() => setShowAddModal(true)}
            className="inline-flex items-center gap-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg font-medium transition-colors"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            Thêm bác sĩ
          </button>
        </div>

        {/* Search */}
        <form onSubmit={handleSearch} className="mb-6">
          <div className="relative max-w-md">
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Tìm kiếm theo tên, email..."
              className="w-full pl-10 pr-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
            />
            <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-slate-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
          </div>
        </form>

        {/* Doctors List */}
        {loading ? (
          <div className="flex items-center justify-center py-20">
            <div className="w-8 h-8 border-4 border-indigo-500 border-t-transparent rounded-full animate-spin"></div>
          </div>
        ) : doctors.length === 0 ? (
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-12 text-center">
            <svg className="w-16 h-16 text-slate-300 dark:text-slate-600 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5.121 17.804A13.937 13.937 0 0112 16c2.5 0 4.847.655 6.879 1.804M15 10a3 3 0 11-6 0 3 3 0 016 0zm6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-2">Chưa có bác sĩ nào</h3>
            <p className="text-slate-500 dark:text-slate-400 mb-4">Thêm bác sĩ để bắt đầu quản lý bệnh nhân</p>
            <button
              onClick={() => setShowAddModal(true)}
              className="inline-flex items-center gap-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg font-medium"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
              </svg>
              Thêm bác sĩ đầu tiên
            </button>
          </div>
        ) : (
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full">
                <thead className="bg-slate-50 dark:bg-slate-800/50">
                  <tr>
                    <th className="text-left px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Bác sĩ</th>
                    <th className="text-left px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Chuyên khoa</th>
                    <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Bệnh nhân</th>
                    <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Phân tích</th>
                    <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Trạng thái</th>
                    <th className="text-right px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Thao tác</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-200 dark:divide-slate-700">
                  {doctors.map((doctor) => (
                    <tr key={doctor.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/50">
                      <td className="px-6 py-4">
                        <div className="flex items-center gap-3">
                          <div className="w-10 h-10 rounded-full bg-indigo-100 dark:bg-indigo-900/30 flex items-center justify-center text-indigo-600 dark:text-indigo-400 font-semibold">
                            {doctor.fullName.charAt(0).toUpperCase()}
                          </div>
                          <div>
                            <div className="flex items-center gap-2">
                              <p className="font-medium text-slate-900 dark:text-white">{doctor.fullName}</p>
                              {doctor.isPrimary && (
                                <span className="px-2 py-0.5 bg-yellow-100 dark:bg-yellow-900/30 text-yellow-700 dark:text-yellow-400 text-xs rounded-full">
                                  Chính
                                </span>
                              )}
                            </div>
                            <p className="text-sm text-slate-500 dark:text-slate-400">{doctor.email}</p>
                          </div>
                        </div>
                      </td>
                      <td className="px-6 py-4">
                        <span className="text-slate-600 dark:text-slate-300">
                          {doctor.specialization || 'Chưa cập nhật'}
                        </span>
                      </td>
                      <td className="px-6 py-4 text-center">
                        <span className="text-slate-900 dark:text-white font-medium">{doctor.patientCount}</span>
                      </td>
                      <td className="px-6 py-4 text-center">
                        <span className="text-slate-900 dark:text-white font-medium">{doctor.analysisCount}</span>
                      </td>
                      <td className="px-6 py-4 text-center">
                        <span className={`inline-flex px-2 py-1 text-xs font-medium rounded-full ${
                          doctor.isActive
                            ? 'bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-400'
                            : 'bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-400'
                        }`}>
                          {doctor.isActive ? 'Hoạt động' : 'Vô hiệu'}
                        </span>
                      </td>
                      <td className="px-6 py-4 text-right">
                        <div className="flex items-center justify-end gap-2">
                          {!doctor.isPrimary && (
                            <button
                              onClick={() => handleSetPrimary(doctor.doctorId)}
                              className="p-2 text-yellow-600 hover:bg-yellow-100 dark:hover:bg-yellow-900/30 rounded-lg transition-colors"
                              title="Đặt làm bác sĩ chính"
                            >
                              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11.049 2.927c.3-.921 1.603-.921 1.902 0l1.519 4.674a1 1 0 00.95.69h4.915c.969 0 1.371 1.24.588 1.81l-3.976 2.888a1 1 0 00-.363 1.118l1.518 4.674c.3.922-.755 1.688-1.538 1.118l-3.976-2.888a1 1 0 00-1.176 0l-3.976 2.888c-.783.57-1.838-.197-1.538-1.118l1.518-4.674a1 1 0 00-.363-1.118l-3.976-2.888c-.784-.57-.38-1.81.588-1.81h4.914a1 1 0 00.951-.69l1.519-4.674z" />
                              </svg>
                            </button>
                          )}
                          <button
                            onClick={() => handleToggleActive(doctor.doctorId, doctor.isActive)}
                            className={`p-2 rounded-lg transition-colors ${
                              doctor.isActive
                                ? 'text-slate-600 hover:bg-slate-100 dark:hover:bg-slate-800'
                                : 'text-green-600 hover:bg-green-100 dark:hover:bg-green-900/30'
                            }`}
                            title={doctor.isActive ? 'Vô hiệu hóa' : 'Kích hoạt'}
                          >
                            {doctor.isActive ? (
                              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                              </svg>
                            ) : (
                              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                              </svg>
                            )}
                          </button>
                          <button
                            onClick={() => setShowDeleteModal(doctor.doctorId)}
                            className="p-2 text-red-600 hover:bg-red-100 dark:hover:bg-red-900/30 rounded-lg transition-colors"
                            title="Xóa khỏi phòng khám"
                          >
                            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                            </svg>
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </main>

      {/* Add Doctor Modal */}
      {showAddModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl w-full max-w-md p-6">
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Thêm bác sĩ mới</h3>
            <form onSubmit={handleAddDoctor} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Họ và tên <span className="text-red-500">*</span>
                </label>
                <input
                  type="text"
                  value={newDoctor.fullName}
                  onChange={(e) => setNewDoctor({ ...newDoctor, fullName: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="Nguyễn Văn A"
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Email <span className="text-red-500">*</span>
                </label>
                <input
                  type="email"
                  value={newDoctor.email}
                  onChange={(e) => setNewDoctor({ ...newDoctor, email: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="doctor@example.com"
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Số điện thoại
                </label>
                <input
                  type="tel"
                  value={newDoctor.phone}
                  onChange={(e) => setNewDoctor({ ...newDoctor, phone: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="0123 456 789"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Chuyên khoa
                </label>
                <input
                  type="text"
                  value={newDoctor.specialization}
                  onChange={(e) => setNewDoctor({ ...newDoctor, specialization: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="Nhãn khoa"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Mật khẩu (tùy chọn)
                </label>
                <input
                  type="password"
                  value={newDoctor.password}
                  onChange={(e) => setNewDoctor({ ...newDoctor, password: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="Nhập mật khẩu để tạo tài khoản đăng nhập"
                />
                <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  Nếu nhập mật khẩu, hệ thống sẽ tạo tài khoản để bác sĩ có thể đăng nhập vào trang của họ. Để trống nếu chỉ thêm vào phòng khám.
                </p>
              </div>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={newDoctor.isPrimary}
                  onChange={(e) => setNewDoctor({ ...newDoctor, isPrimary: e.target.checked })}
                  className="w-4 h-4 rounded border-slate-300 dark:border-slate-700 text-indigo-600"
                />
                <span className="text-sm text-slate-600 dark:text-slate-400">Đặt làm bác sĩ chính</span>
              </label>
              <div className="flex gap-3 pt-4">
                <button
                  type="button"
                  onClick={() => setShowAddModal(false)}
                  className="flex-1 px-4 py-2 border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800"
                >
                  Hủy
                </button>
                <button
                  type="submit"
                  disabled={addLoading}
                  className="flex-1 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg disabled:opacity-50"
                >
                  {addLoading ? 'Đang thêm...' : 'Thêm bác sĩ'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Delete Confirmation Modal */}
      {showDeleteModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl w-full max-w-sm p-6 text-center">
            <div className="w-12 h-12 bg-red-100 dark:bg-red-900/30 rounded-full flex items-center justify-center mx-auto mb-4">
              <svg className="w-6 h-6 text-red-600 dark:text-red-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
            </div>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-2">Xác nhận xóa</h3>
            <p className="text-slate-600 dark:text-slate-400 mb-6">
              Bạn có chắc muốn xóa bác sĩ này khỏi phòng khám?
            </p>
            <div className="flex gap-3">
              <button
                onClick={() => setShowDeleteModal(null)}
                className="flex-1 px-4 py-2 border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800"
              >
                Hủy
              </button>
              <button
                onClick={() => handleRemoveDoctor(showDeleteModal)}
                className="flex-1 px-4 py-2 bg-red-600 hover:bg-red-700 text-white rounded-lg"
              >
                Xóa
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default ClinicDoctorsPage;
