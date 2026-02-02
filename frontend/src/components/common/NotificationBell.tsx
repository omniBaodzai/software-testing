import React, { useEffect, useState, useRef } from "react";
import { useNavigate } from "react-router-dom";
import { useNotificationStore } from "../../store/notificationStore";
import {
  connectNotificationsSSE,
  startPolling,
} from "../../services/notificationService";
import medicalNotesService, {
  MedicalNote,
} from "../../services/medicalNotesService";
import type { Notification } from "../../types/notification";

/** Hiển thị thời gian dạng "X phút trước", "X giờ trước", "X ngày trước". Trả về "Vừa xong" nếu không có hoặc sai định dạng. */
const timeAgo = (iso?: string) => {
  if (!iso || typeof iso !== "string") return "Vừa xong";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "Vừa xong";
  const diff = Date.now() - d.getTime();
  if (diff < 0) return "Vừa xong";
  const s = Math.floor(diff / 1000);
  if (s < 60) return `${s} giây trước`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} phút trước`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} giờ trước`;
  const days = Math.floor(h / 24);
  if (days === 1) return "1 ngày trước";
  return `${days} ngày trước`;
};

const NotificationBell: React.FC = () => {
  const navigate = useNavigate();
  const { notifications, unreadCount, load, setFromArray, markRead, markAllRead, add } =
    useNotificationStore();
  const [open, setOpen] = useState(false);
  const [modalNotification, setModalNotification] =
    useState<Notification | null>(null);
  const [noteDetail, setNoteDetail] = useState<MedicalNote | null>(null);
  const [loadingNote, setLoadingNote] = useState(false);
  const dropdownRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    load();

    // Kết nối SSE để nhận thông báo real-time
    const es = connectNotificationsSSE((n: Notification) => {
      add(n);
    });

    let stopPolling: (() => void) | null = null;
    const startPollingFallback = () => {
      if (stopPolling) return;
      stopPolling = startPolling((arr) => {
        setFromArray(arr);
      });
    };

    if (!es) {
      startPollingFallback();
    } else {
      const origOnError = es.onerror;
      es.onerror = (ev) => {
        origOnError?.call(es, ev);
        // Khi SSE lỗi (vd. 401), fallback sang polling
        startPollingFallback();
      };
    }

    const onClick = (ev: MouseEvent) => {
      if (
        dropdownRef.current &&
        !dropdownRef.current.contains(ev.target as Node)
      ) {
        setOpen(false);
      }
    };
    document.addEventListener("click", onClick);

    return () => {
      es?.close?.();
      if (stopPolling) stopPolling();
      document.removeEventListener("click", onClick);
    };
  }, []);

  const handleToggle = () => {
    setOpen((v: boolean) => {
      const next = !v;
      // Khi mở dropdown: gọi lại API để danh sách và badge luôn đồng bộ
      if (next) load();
      return next;
    });
  };

  const handleMarkAllRead = async () => {
    await markAllRead();
  };

  const handleNotificationClick = async (n: Notification) => {
    await markRead(n.id);
    setModalNotification(n);
    setNoteDetail(null);
    setLoadingNote(false);
    const noteId =
      n.data && typeof n.data === "object" && "noteId" in n.data
        ? String((n.data as { noteId?: string }).noteId)
        : null;
    if (noteId) {
      setLoadingNote(true);
      try {
        const note = await medicalNotesService.getMyNoteById(noteId);
        setNoteDetail(note);
        await medicalNotesService.markNoteAsViewed(noteId);
      } catch {
        // Không có quyền hoặc không tìm thấy (vd. user là bác sĩ) → chỉ hiển thị title + message
      } finally {
        setLoadingNote(false);
      }
    }
  };

  const closeModal = () => {
    setModalNotification(null);
    setNoteDetail(null);
  };

  return (
    <div className="relative" ref={dropdownRef}>
      <button
        className="p-2 rounded-full hover:bg-slate-100 dark:hover:bg-slate-800 relative text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
        onClick={handleToggle}
        aria-label="Thông báo"
      >
        <svg
          xmlns="http://www.w3.org/2000/svg"
          className="h-6 w-6"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6 6 0 10-12 0v3.159c0 .538-.214 1.055-.595 1.436L4 17h11z"
          />
        </svg>
        {unreadCount > 0 && (
          <span className="absolute -top-0.5 -right-0.5 bg-red-500 text-white text-xs rounded-full min-w-[18px] h-[18px] px-1 flex items-center justify-center font-bold">
            {unreadCount > 9 ? "9+" : unreadCount}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 mt-2 w-80 bg-white dark:bg-slate-800 border border-slate-200 dark:border-slate-700 rounded-lg shadow-xl z-50">
          <div className="flex items-center justify-between px-4 py-2 border-b border-slate-200 dark:border-slate-700">
            <strong className="text-slate-900 dark:text-white">
              Thông báo
            </strong>
            <button
              className="text-sm text-blue-600 dark:text-blue-400 hover:underline"
              onClick={handleMarkAllRead}
            >
              Đánh dấu đã đọc
            </button>
          </div>
          <div className="max-h-60 overflow-auto">
            {notifications.length === 0 && (
              <div className="p-4 text-sm text-slate-500 dark:text-slate-400">
                Không có thông báo
              </div>
            )}
            {notifications.map((n) => (
              <div
                key={n.id}
                className={`p-3 cursor-pointer flex justify-between items-start border-b border-slate-100 dark:border-slate-700/50 last:border-0 ${
                  n.read
                    ? "bg-white dark:bg-slate-800 hover:bg-slate-50 dark:hover:bg-slate-700/50"
                    : "bg-slate-50 dark:bg-slate-800/80 hover:bg-slate-100 dark:hover:bg-slate-700/50"
                }`}
                onClick={() => handleNotificationClick(n)}
              >
                <div className="min-w-0 flex-1">
                  <div className="text-sm font-medium text-slate-900 dark:text-white">
                    {n.title}
                  </div>
                  <div className="text-xs text-slate-600 dark:text-slate-400 mt-0.5">
                    {n.message}
                  </div>
                </div>
                <div className="text-xs text-slate-400 dark:text-slate-500 ml-2 shrink-0">
                  {timeAgo(n.createdAt)}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Modal xem nội dung thông báo / ghi chú */}
      {modalNotification && (
        <div
          className="fixed inset-0 z-[100] flex items-center justify-center p-4 bg-black/50"
          onClick={closeModal}
          role="dialog"
          aria-modal="true"
          aria-labelledby="notification-modal-title"
        >
          <div
            className="bg-white dark:bg-slate-800 rounded-xl shadow-xl max-w-lg w-full max-h-[85vh] overflow-hidden flex flex-col border border-slate-200 dark:border-slate-700"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-center justify-between px-4 py-3 border-b border-slate-200 dark:border-slate-700 shrink-0">
              <h2
                id="notification-modal-title"
                className="text-lg font-semibold text-slate-900 dark:text-white"
              >
                {modalNotification.title}
              </h2>
              <button
                type="button"
                onClick={closeModal}
                className="p-1.5 rounded-lg text-slate-500 hover:text-slate-700 dark:hover:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-700"
                aria-label="Đóng"
              >
                <svg
                  className="w-5 h-5"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M6 18L18 6M6 6l12 12"
                  />
                </svg>
              </button>
            </div>
            <div className="px-4 py-3 overflow-y-auto flex-1">
              {loadingNote ? (
                <p className="text-sm text-slate-500 dark:text-slate-400">
                  Đang tải nội dung...
                </p>
              ) : noteDetail ? (
                <div className="space-y-3 text-sm">
                  <div>
                    <p className="font-medium text-slate-700 dark:text-slate-300 mb-1">
                      Nội dung ghi chú
                    </p>
                    <p className="text-slate-900 dark:text-white whitespace-pre-wrap">
                      {noteDetail.noteContent || "—"}
                    </p>
                  </div>
                  {noteDetail.diagnosis && (
                    <div>
                      <p className="font-medium text-slate-700 dark:text-slate-300 mb-1">
                        Chẩn đoán
                      </p>
                      <p className="text-slate-900 dark:text-white whitespace-pre-wrap">
                        {noteDetail.diagnosis}
                      </p>
                    </div>
                  )}
                  {noteDetail.treatmentPlan && (
                    <div>
                      <p className="font-medium text-slate-700 dark:text-slate-300 mb-1">
                        Kế hoạch điều trị
                      </p>
                      <p className="text-slate-900 dark:text-white whitespace-pre-wrap">
                        {noteDetail.treatmentPlan}
                      </p>
                    </div>
                  )}
                  {noteDetail.clinicalObservations && (
                    <div>
                      <p className="font-medium text-slate-700 dark:text-slate-300 mb-1">
                        Quan sát lâm sàng
                      </p>
                      <p className="text-slate-900 dark:text-white whitespace-pre-wrap">
                        {noteDetail.clinicalObservations}
                      </p>
                    </div>
                  )}
                  {noteDetail.doctorName && (
                    <p className="text-slate-500 dark:text-slate-400 text-xs">
                      Bác sĩ: {noteDetail.doctorName}
                    </p>
                  )}
                </div>
              ) : (
                <p className="text-slate-700 dark:text-slate-300">
                  {modalNotification.message}
                </p>
              )}
            </div>
            <div className="px-4 py-3 border-t border-slate-200 dark:border-slate-700 flex gap-2 justify-end shrink-0">
              {noteDetail && (
                <button
                  type="button"
                  onClick={() => {
                    closeModal();
                    navigate("/notes");
                  }}
                  className="px-3 py-1.5 text-sm text-blue-600 dark:text-blue-400 hover:underline"
                >
                  Xem trang Ghi chú y tế
                </button>
              )}
              <button
                type="button"
                onClick={closeModal}
                className="px-4 py-2 bg-slate-200 dark:bg-slate-600 text-slate-800 dark:text-slate-200 rounded-lg hover:bg-slate-300 dark:hover:bg-slate-500 font-medium"
              >
                Đóng
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default NotificationBell;
