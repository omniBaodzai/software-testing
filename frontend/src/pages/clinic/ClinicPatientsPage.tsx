import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import ClinicHeader from '../../components/clinic/ClinicHeader';
import clinicAuthService from '../../services/clinicAuthService';
import clinicManagementService, { ClinicPatient, ClinicDoctor, RegisterPatientData } from '../../services/clinicManagementService';
import toast from 'react-hot-toast';

const ClinicPatientsPage = () => {
  const navigate = useNavigate();
  const [loading, setLoading] = useState(true);
  const [patients, setPatients] = useState<ClinicPatient[]>([]);
  const [doctors, setDoctors] = useState<ClinicDoctor[]>([]);
  const [search, setSearch] = useState('');
  const [filterDoctor, setFilterDoctor] = useState('');
  const [filterRisk, setFilterRisk] = useState('');
  const [showAddModal, setShowAddModal] = useState(false);
  const [showAssignModal, setShowAssignModal] = useState<string | null>(null);
  const [showDeleteModal, setShowDeleteModal] = useState<string | null>(null);
  const [addLoading, setAddLoading] = useState(false);
  const [newPatient, setNewPatient] = useState<RegisterPatientData>({
    email: '',
    fullName: '',
    phone: '',
    gender: '',
    address: '',
    password: '',
  });

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
      const [patientsData, doctorsData] = await Promise.all([
        clinicManagementService.getPatients({
          search: search || undefined,
          doctorId: filterDoctor || undefined,
          riskLevel: filterRisk || undefined,
        }),
        clinicManagementService.getDoctors(),
      ]);
      setPatients(patientsData);
      setDoctors(doctorsData);
    } catch (error: any) {
      console.error('Error fetching data:', error);
      if (error.response?.status === 401) {
        navigate('/login');
      }
    } finally {
      setLoading(false);
    }
  };

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    fetchData();
  };

  useEffect(() => {
    fetchData();
  }, [filterDoctor, filterRisk]);

  const handleAddPatient = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newPatient.email || !newPatient.fullName) {
      toast.error('Vui lòng nhập đầy đủ thông tin');
      return;
    }

    setAddLoading(true);
    try {
      await clinicManagementService.registerPatient(newPatient);
      toast.success('Đăng ký bệnh nhân thành công!');
      setShowAddModal(false);
      setNewPatient({ email: '', fullName: '', phone: '', gender: '', address: '', password: '' });
      fetchData();
    } catch (error: any) {
      const message = error.response?.data?.message || 'Không thể đăng ký bệnh nhân';
      toast.error(message);
    } finally {
      setAddLoading(false);
    }
  };

  const handleAssignDoctor = async (patientId: string, doctorId: string) => {
    try {
      await clinicManagementService.assignDoctorToPatient(patientId, doctorId, true);
      toast.success('Đã gán bác sĩ cho bệnh nhân');
      setShowAssignModal(null);
      fetchData();
    } catch (error) {
      toast.error('Không thể gán bác sĩ');
    }
  };

  const handleRemovePatient = async (patientId: string) => {
    try {
      await clinicManagementService.removePatient(patientId);
      toast.success('Đã xóa bệnh nhân khỏi phòng khám');
      setShowDeleteModal(null);
      fetchData();
    } catch (error) {
      toast.error('Không thể xóa bệnh nhân');
    }
  };

  const getRiskBadge = (risk?: string) => {
    switch (risk) {
      case 'Critical':
        return 'bg-purple-100 dark:bg-purple-900/30 text-purple-700 dark:text-purple-400';
      case 'High':
        return 'bg-red-100 dark:bg-red-900/30 text-red-700 dark:text-red-400';
      case 'Medium':
        return 'bg-yellow-100 dark:bg-yellow-900/30 text-yellow-700 dark:text-yellow-400';
      case 'Low':
        return 'bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-400';
      default:
        return 'bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-400';
    }
  };

  const getRiskText = (risk?: string) => {
    switch (risk) {
      case 'Critical': return 'Nghiêm trọng';
      case 'High': return 'Cao';
      case 'Medium': return 'Trung bình';
      case 'Low': return 'Thấp';
      default: return 'Chưa có';
    }
  };

  return (
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950">
      <ClinicHeader />
      
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {/* Header */}
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
          <div>
            <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Quản lý bệnh nhân</h1>
            <p className="text-slate-600 dark:text-slate-400 mt-1">
              {patients.length} bệnh nhân trong phòng khám
            </p>
          </div>
          <button
            onClick={() => setShowAddModal(true)}
            className="inline-flex items-center gap-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg font-medium transition-colors"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            Đăng ký bệnh nhân
          </button>
        </div>

        {/* Filters */}
        <div className="flex flex-col sm:flex-row gap-4 mb-6">
          <form onSubmit={handleSearch} className="flex-1">
            <div className="relative">
              <input
                type="text"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Tìm kiếm theo tên, email, SĐT..."
                className="w-full pl-10 pr-4 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
              />
              <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-slate-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
            </div>
          </form>

          <select
            value={filterDoctor}
            onChange={(e) => setFilterDoctor(e.target.value)}
            className="px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
          >
            <option value="">Tất cả bác sĩ</option>
            {doctors.map((doc) => (
              <option key={doc.doctorId} value={doc.doctorId}>{doc.fullName}</option>
            ))}
          </select>

          <select
            value={filterRisk}
            onChange={(e) => setFilterRisk(e.target.value)}
            className="px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
          >
            <option value="">Tất cả mức rủi ro</option>
            <option value="Critical">Nghiêm trọng</option>
            <option value="High">Cao</option>
            <option value="Medium">Trung bình</option>
            <option value="Low">Thấp</option>
          </select>
        </div>

        {/* Patients List */}
        {loading ? (
          <div className="flex items-center justify-center py-20">
            <div className="w-8 h-8 border-4 border-indigo-500 border-t-transparent rounded-full animate-spin"></div>
          </div>
        ) : patients.length === 0 ? (
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 p-12 text-center">
            <svg className="w-16 h-16 text-slate-300 dark:text-slate-600 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-2">Chưa có bệnh nhân nào</h3>
            <p className="text-slate-500 dark:text-slate-400 mb-4">Đăng ký bệnh nhân để bắt đầu theo dõi</p>
            <button
              onClick={() => setShowAddModal(true)}
              className="inline-flex items-center gap-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg font-medium"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
              </svg>
              Đăng ký bệnh nhân đầu tiên
            </button>
          </div>
        ) : (
          <div className="bg-white dark:bg-slate-900 rounded-xl shadow-sm border border-slate-200 dark:border-slate-800 overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full">
                <thead className="bg-slate-50 dark:bg-slate-800/50">
                  <tr>
                    <th className="text-left px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Bệnh nhân</th>
                    <th className="text-left px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Bác sĩ phụ trách</th>
                    <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Phân tích</th>
                    <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Rủi ro</th>
                    <th className="text-center px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Lần khám cuối</th>
                    <th className="text-right px-6 py-3 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Thao tác</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-200 dark:divide-slate-700">
                  {patients.map((patient) => (
                    <tr key={patient.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/50">
                      <td className="px-6 py-4">
                        <div className="flex items-center gap-3">
                          <div className="w-10 h-10 rounded-full bg-green-100 dark:bg-green-900/30 flex items-center justify-center text-green-600 dark:text-green-400 font-semibold">
                            {patient.fullName.charAt(0).toUpperCase()}
                          </div>
                          <div>
                            <p className="font-medium text-slate-900 dark:text-white">{patient.fullName}</p>
                            <p className="text-sm text-slate-500 dark:text-slate-400">{patient.email}</p>
                            {patient.phone && (
                              <p className="text-xs text-slate-400 dark:text-slate-500">{patient.phone}</p>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="px-6 py-4">
                        {patient.assignedDoctorName ? (
                          <span className="text-slate-700 dark:text-slate-300">{patient.assignedDoctorName}</span>
                        ) : (
                          <button
                            onClick={() => setShowAssignModal(patient.userId)}
                            className="text-indigo-600 hover:text-indigo-700 dark:text-indigo-400 text-sm font-medium"
                          >
                            + Gán bác sĩ
                          </button>
                        )}
                      </td>
                      <td className="px-6 py-4 text-center">
                        <span className="text-slate-900 dark:text-white font-medium">{patient.analysisCount}</span>
                      </td>
                      <td className="px-6 py-4 text-center">
                        <span className={`inline-flex px-2 py-1 text-xs font-medium rounded-full ${getRiskBadge(patient.latestRiskLevel)}`}>
                          {getRiskText(patient.latestRiskLevel)}
                        </span>
                      </td>
                      <td className="px-6 py-4 text-center text-sm text-slate-600 dark:text-slate-400">
                        {patient.lastAnalysisDate
                          ? new Date(patient.lastAnalysisDate).toLocaleDateString('vi-VN')
                          : '-'}
                      </td>
                      <td className="px-6 py-4 text-right">
                        <div className="flex items-center justify-end gap-2">
                          <button
                            onClick={() => setShowAssignModal(patient.userId)}
                            className="p-2 text-indigo-600 hover:bg-indigo-100 dark:hover:bg-indigo-900/30 rounded-lg transition-colors"
                            title="Gán bác sĩ"
                          >
                            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
                            </svg>
                          </button>
                          <button
                            onClick={() => setShowDeleteModal(patient.userId)}
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

      {/* Add Patient Modal */}
      {showAddModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl w-full max-w-md p-6 max-h-[90vh] overflow-y-auto">
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Đăng ký bệnh nhân mới</h3>
            <form onSubmit={handleAddPatient} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Họ và tên <span className="text-red-500">*</span>
                </label>
                <input
                  type="text"
                  value={newPatient.fullName}
                  onChange={(e) => setNewPatient({ ...newPatient, fullName: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="Nguyễn Văn B"
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Email <span className="text-red-500">*</span>
                </label>
                <input
                  type="email"
                  value={newPatient.email}
                  onChange={(e) => setNewPatient({ ...newPatient, email: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="patient@example.com"
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Số điện thoại
                </label>
                <input
                  type="tel"
                  value={newPatient.phone}
                  onChange={(e) => setNewPatient({ ...newPatient, phone: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="0123 456 789"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Giới tính
                </label>
                <select
                  value={newPatient.gender}
                  onChange={(e) => setNewPatient({ ...newPatient, gender: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                >
                  <option value="">Chọn giới tính</option>
                  <option value="Male">Nam</option>
                  <option value="Female">Nữ</option>
                  <option value="Other">Khác</option>
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Địa chỉ
                </label>
                <input
                  type="text"
                  value={newPatient.address}
                  onChange={(e) => setNewPatient({ ...newPatient, address: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="123 Đường ABC, Quận XYZ"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Bác sĩ phụ trách
                </label>
                <select
                  value={newPatient.assignedDoctorId || ''}
                  onChange={(e) => setNewPatient({ ...newPatient, assignedDoctorId: e.target.value || undefined })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                >
                  <option value="">Chưa gán</option>
                  {doctors.map((doc) => (
                    <option key={doc.doctorId} value={doc.doctorId}>{doc.fullName}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">
                  Mật khẩu (tùy chọn)
                </label>
                <input
                  type="password"
                  value={newPatient.password}
                  onChange={(e) => setNewPatient({ ...newPatient, password: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 dark:border-slate-700 rounded-lg bg-white dark:bg-slate-800 text-slate-900 dark:text-white"
                  placeholder="Nhập mật khẩu để tạo tài khoản đăng nhập"
                />
                <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  Nếu nhập mật khẩu, hệ thống sẽ tạo tài khoản để bệnh nhân có thể đăng nhập vào trang của họ. Để trống nếu chỉ thêm vào phòng khám.
                </p>
              </div>
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
                  {addLoading ? 'Đang đăng ký...' : 'Đăng ký'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Assign Doctor Modal */}
      {showAssignModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl w-full max-w-sm p-6">
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Gán bác sĩ phụ trách</h3>
            <div className="space-y-2 max-h-60 overflow-y-auto">
              {doctors.map((doctor) => (
                <button
                  key={doctor.doctorId}
                  onClick={() => handleAssignDoctor(showAssignModal, doctor.doctorId)}
                  className="w-full flex items-center gap-3 p-3 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors text-left"
                >
                  <div className="w-10 h-10 rounded-full bg-indigo-100 dark:bg-indigo-900/30 flex items-center justify-center text-indigo-600 dark:text-indigo-400 font-semibold">
                    {doctor.fullName.charAt(0).toUpperCase()}
                  </div>
                  <div>
                    <p className="font-medium text-slate-900 dark:text-white">{doctor.fullName}</p>
                    <p className="text-sm text-slate-500 dark:text-slate-400">
                      {doctor.specialization || 'Chưa cập nhật chuyên khoa'}
                    </p>
                  </div>
                </button>
              ))}
            </div>
            <button
              onClick={() => setShowAssignModal(null)}
              className="w-full mt-4 px-4 py-2 border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800"
            >
              Hủy
            </button>
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
              Bạn có chắc muốn xóa bệnh nhân này khỏi phòng khám?
            </p>
            <div className="flex gap-3">
              <button
                onClick={() => setShowDeleteModal(null)}
                className="flex-1 px-4 py-2 border border-slate-300 dark:border-slate-700 text-slate-700 dark:text-slate-300 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800"
              >
                Hủy
              </button>
              <button
                onClick={() => handleRemovePatient(showDeleteModal)}
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

export default ClinicPatientsPage;
