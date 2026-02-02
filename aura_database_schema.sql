-- =====================================================
-- AURA Retinal Screening System Database Schema
-- System: SP26SE025
-- Database: PostgreSQL
-- Version: 2.1 (Fixed dependency order for Admins table)
-- Created: 2025
-- =====================================================

-- Drop existing tables if they exist (CASCADE ensures dependent tables are dropped too)
DROP TABLE IF EXISTS exported_reports CASCADE;
DROP TABLE IF EXISTS medical_notes CASCADE;
DROP TABLE IF EXISTS clinic_reports CASCADE;
DROP TABLE IF EXISTS ai_configurations CASCADE;
DROP TABLE IF EXISTS notification_templates CASCADE;
DROP TABLE IF EXISTS bulk_upload_batches CASCADE;
DROP TABLE IF EXISTS audit_logs CASCADE;
DROP TABLE IF EXISTS notifications CASCADE;
DROP TABLE IF EXISTS messages CASCADE;
DROP TABLE IF EXISTS payment_history CASCADE;
DROP TABLE IF EXISTS user_packages CASCADE;
DROP TABLE IF EXISTS service_packages CASCADE;
DROP TABLE IF EXISTS annotations CASCADE;
DROP TABLE IF EXISTS analysis_results CASCADE;
DROP TABLE IF EXISTS retinal_images CASCADE;
DROP TABLE IF EXISTS patient_doctor_assignments CASCADE;
DROP TABLE IF EXISTS clinic_admins CASCADE;
DROP TABLE IF EXISTS clinic_doctors CASCADE;
DROP TABLE IF EXISTS clinic_users CASCADE;
DROP TABLE IF EXISTS ai_feedback CASCADE;
DROP TABLE IF EXISTS ai_model_versions CASCADE;
DROP TABLE IF EXISTS user_roles CASCADE;
DROP TABLE IF EXISTS users CASCADE;
DROP TABLE IF EXISTS doctors CASCADE;
DROP TABLE IF EXISTS clinics CASCADE;
DROP TABLE IF EXISTS admins CASCADE;
DROP TABLE IF EXISTS role_permissions CASCADE;
DROP TABLE IF EXISTS permissions CASCADE;
DROP TABLE IF EXISTS roles CASCADE;

-- =====================================================
-- 1. ROLES AND PERMISSIONS
-- =====================================================

CREATE TABLE roles (
    Id VARCHAR(255) PRIMARY KEY,
    RoleName VARCHAR(255),
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

CREATE TABLE permissions (
    Id VARCHAR(255) PRIMARY KEY,
    PermissionName VARCHAR(255) NOT NULL UNIQUE,
    PermissionDescription TEXT,
    ResourceType VARCHAR(255),
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

CREATE TABLE role_permissions (
    Id VARCHAR(255) PRIMARY KEY,
    RoleId VARCHAR(255) NOT NULL REFERENCES roles(Id) ON DELETE CASCADE,
    PermissionId VARCHAR(255) NOT NULL REFERENCES permissions(Id) ON DELETE CASCADE,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255),
    UNIQUE(RoleId, PermissionId)
);

-- =====================================================
-- 2. ADMINS (MOVED UP TO FIX DEPENDENCY ERROR)
-- =====================================================
-- Admins must be created before Clinics because Clinics reference Admins (VerifiedBy)

CREATE TABLE admins (
    Id VARCHAR(255) PRIMARY KEY,
    Username VARCHAR(255),
    Password VARCHAR(255),
    FirstName VARCHAR(255),
    LastName VARCHAR(255),
    Email VARCHAR(255) NOT NULL UNIQUE,
    Phone VARCHAR(255),
    ProfileImageUrl VARCHAR(500),
    RoleId VARCHAR(255) NOT NULL REFERENCES roles(Id),
    IsSuperAdmin BOOLEAN DEFAULT FALSE,
    IsActive BOOLEAN DEFAULT TRUE,
    LastLoginAt TIMESTAMP,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 3. USERS (PATIENTS)
-- =====================================================

CREATE TABLE users (
    Id VARCHAR(255) PRIMARY KEY,
    Username VARCHAR(255),
    Password VARCHAR(255),
    FirstName VARCHAR(255),
    LastName VARCHAR(255),
    Email VARCHAR(255) NOT NULL UNIQUE,
    Dob DATE,
    Phone VARCHAR(255),
    Gender VARCHAR(10) CHECK (Gender IN ('Male', 'Female', 'Other')),
    Address VARCHAR(255),
    City VARCHAR(100),
    Province VARCHAR(100),
    Country VARCHAR(100) DEFAULT 'Vietnam',
    IdentificationNumber VARCHAR(50),
    AuthenticationProvider VARCHAR(50) DEFAULT 'email' CHECK (AuthenticationProvider IN ('email', 'google', 'facebook')),
    ProviderUserId VARCHAR(255),
    ProfileImageUrl VARCHAR(500),
    IsEmailVerified BOOLEAN DEFAULT FALSE,
    IsActive BOOLEAN DEFAULT TRUE,
    LastLoginAt TIMESTAMP,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 4. USER ROLES (JUNCTION TABLE)
-- =====================================================

CREATE TABLE user_roles (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) NOT NULL REFERENCES users(Id) ON DELETE CASCADE,
    RoleId VARCHAR(255) NOT NULL REFERENCES roles(Id) ON DELETE CASCADE,
    IsPrimary BOOLEAN DEFAULT FALSE,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255),
    UNIQUE(UserId, RoleId)
);

-- =====================================================
-- 5. DOCTORS
-- =====================================================

CREATE TABLE doctors (
    Id VARCHAR(255) PRIMARY KEY,
    Username VARCHAR(255),
    Password VARCHAR(255),
    FirstName VARCHAR(255),
    LastName VARCHAR(255),
    Email VARCHAR(255) NOT NULL UNIQUE,
    Phone VARCHAR(255),
    Gender VARCHAR(10) CHECK (Gender IN ('Male', 'Female', 'Other')),
    LicenseNumber VARCHAR(100) NOT NULL UNIQUE,
    Specialization VARCHAR(255),
    YearsOfExperience INTEGER,
    Qualification TEXT,
    HospitalAffiliation VARCHAR(255),
    ProfileImageUrl VARCHAR(500),
    Bio TEXT,
    IsVerified BOOLEAN DEFAULT FALSE,
    IsActive BOOLEAN DEFAULT TRUE,
    LastLoginAt TIMESTAMP,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 6. CLINICS
-- =====================================================

CREATE TABLE clinics (
    Id VARCHAR(255) PRIMARY KEY,
    ClinicName VARCHAR(255) NOT NULL,
    RegistrationNumber VARCHAR(100) UNIQUE,
    TaxCode VARCHAR(50),
    Email VARCHAR(255) NOT NULL UNIQUE,
    Phone VARCHAR(255),
    Address VARCHAR(255) NOT NULL,
    City VARCHAR(100),
    Province VARCHAR(100),
    Country VARCHAR(100) DEFAULT 'Vietnam',
    WebsiteUrl VARCHAR(500),
    ContactPersonName VARCHAR(255),
    ContactPersonPhone VARCHAR(255),
    ClinicType VARCHAR(50) CHECK (ClinicType IN ('Hospital', 'Clinic', 'Medical Center', 'Other')),
    VerificationStatus VARCHAR(50) DEFAULT 'Pending' CHECK (VerificationStatus IN ('Pending', 'Approved', 'Rejected', 'Suspended')),
    IsActive BOOLEAN DEFAULT TRUE,
    VerifiedAt TIMESTAMP,
    VerifiedBy VARCHAR(255) REFERENCES admins(Id), -- Now this works because admins exists
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 6.1 CLINIC ADMINS (Separate authentication for clinics)
-- =====================================================

CREATE TABLE clinic_admins (
    Id VARCHAR(255) PRIMARY KEY,
    ClinicId VARCHAR(255) NOT NULL REFERENCES clinics(Id) ON DELETE CASCADE,
    Email VARCHAR(255) NOT NULL UNIQUE,
    PasswordHash VARCHAR(500) NOT NULL,
    FullName VARCHAR(255) NOT NULL,
    Phone VARCHAR(50),
    Role VARCHAR(50) DEFAULT 'ClinicAdmin' CHECK (Role IN ('ClinicAdmin', 'ClinicManager', 'ClinicStaff')),
    IsActive BOOLEAN DEFAULT TRUE,
    LastLoginAt TIMESTAMP,
    PasswordResetToken VARCHAR(500),
    PasswordResetExpires TIMESTAMP,
    RefreshToken VARCHAR(500),
    RefreshTokenExpires TIMESTAMP,
    CreatedDate TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CreatedBy VARCHAR(255),
    UpdatedDate TIMESTAMP,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- Index for clinic_admins
CREATE INDEX idx_clinic_admins_clinic_id ON clinic_admins(ClinicId);
CREATE INDEX idx_clinic_admins_email ON clinic_admins(Email);
CREATE INDEX idx_clinic_admins_is_active ON clinic_admins(IsActive);

-- =====================================================
-- 7. CLINIC RELATIONSHIPS
-- =====================================================

CREATE TABLE clinic_doctors (
    Id VARCHAR(255) PRIMARY KEY,
    ClinicId VARCHAR(255) NOT NULL REFERENCES clinics(Id) ON DELETE CASCADE,
    DoctorId VARCHAR(255) NOT NULL REFERENCES doctors(Id) ON DELETE CASCADE,
    IsPrimary BOOLEAN DEFAULT FALSE,
    JoinedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    IsActive BOOLEAN DEFAULT TRUE,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255),
    UNIQUE(ClinicId, DoctorId)
);

CREATE TABLE clinic_users (
    Id VARCHAR(255) PRIMARY KEY,
    ClinicId VARCHAR(255) NOT NULL REFERENCES clinics(Id) ON DELETE CASCADE,
    UserId VARCHAR(255) NOT NULL REFERENCES users(Id) ON DELETE CASCADE,
    RegisteredAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    IsActive BOOLEAN DEFAULT TRUE,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255),
    UNIQUE(ClinicId, UserId)
);

CREATE TABLE patient_doctor_assignments (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) NOT NULL REFERENCES users(Id) ON DELETE CASCADE,
    DoctorId VARCHAR(255) NOT NULL REFERENCES doctors(Id) ON DELETE CASCADE,
    ClinicId VARCHAR(255) REFERENCES clinics(Id) ON DELETE SET NULL,
    IsPrimary BOOLEAN DEFAULT FALSE,
    AssignedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    AssignedBy VARCHAR(255) REFERENCES admins(Id),
    IsActive BOOLEAN DEFAULT TRUE,
    Notes TEXT,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE
);

-- =====================================================
-- 8. RETINAL IMAGES
-- =====================================================

CREATE TABLE retinal_images (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) NOT NULL REFERENCES users(Id) ON DELETE CASCADE,
    ClinicId VARCHAR(255) REFERENCES clinics(Id) ON DELETE SET NULL,
    DoctorId VARCHAR(255) REFERENCES doctors(Id) ON DELETE SET NULL,
    OriginalFilename VARCHAR(255) NOT NULL,
    StoredFilename VARCHAR(255) NOT NULL,
    FilePath VARCHAR(500) NOT NULL,
    CloudinaryUrl VARCHAR(500),
    FileSize BIGINT,
    ImageType VARCHAR(50) CHECK (ImageType IN ('Fundus', 'OCT')),
    ImageFormat VARCHAR(10) CHECK (ImageFormat IN ('JPEG', 'PNG', 'TIFF', 'DICOM')),
    CaptureDevice VARCHAR(255),
    CaptureDate TIMESTAMP,
    EyeSide VARCHAR(10) CHECK (EyeSide IN ('Left', 'Right', 'Both')),
    UploadStatus VARCHAR(50) DEFAULT 'Uploaded' CHECK (UploadStatus IN ('Uploaded', 'Processing', 'Processed', 'Failed')),
    UploadedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ProcessedAt TIMESTAMP,
    Metadata JSONB,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE
);

-- =====================================================
-- 9. AI MODEL VERSIONS
-- =====================================================

CREATE TABLE ai_model_versions (
    Id VARCHAR(255) PRIMARY KEY,
    ModelName VARCHAR(255) NOT NULL,
    VersionNumber VARCHAR(50) NOT NULL,
    ModelType VARCHAR(100) NOT NULL,
    Description TEXT,
    ModelPath VARCHAR(500),
    AccuracyMetrics JSONB,
    TrainingDate DATE,
    DeployedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    IsActive BOOLEAN DEFAULT TRUE,
    CreatedBy VARCHAR(255) REFERENCES admins(Id),
    CreatedDate DATE,
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255),
    UNIQUE(ModelName, VersionNumber)
);

-- =====================================================
-- 10. ANALYSIS RESULTS
-- =====================================================

CREATE TABLE analysis_results (
    Id VARCHAR(255) PRIMARY KEY,
    ImageId VARCHAR(255) NOT NULL REFERENCES retinal_images(Id) ON DELETE CASCADE,
    UserId VARCHAR(255) NOT NULL REFERENCES users(Id) ON DELETE CASCADE,
    ModelVersionId VARCHAR(255) NOT NULL REFERENCES ai_model_versions(Id),
    AnalysisStatus VARCHAR(50) DEFAULT 'Processing' CHECK (AnalysisStatus IN ('Processing', 'Completed', 'Failed', 'Pending')),
    OverallRiskLevel VARCHAR(50) CHECK (OverallRiskLevel IN ('Low', 'Medium', 'High', 'Critical')),
    RiskScore DECIMAL(5,2) CHECK (RiskScore >= 0 AND RiskScore <= 100),
    
    -- Cardiovascular Risk Assessment
    HypertensionRisk VARCHAR(50) CHECK (HypertensionRisk IN ('Low', 'Medium', 'High')),
    HypertensionScore DECIMAL(5,2),
    
    -- Diabetes Risk Assessment
    DiabetesRisk VARCHAR(50) CHECK (DiabetesRisk IN ('Low', 'Medium', 'High')),
    DiabetesScore DECIMAL(5,2),
    DiabeticRetinopathyDetected BOOLEAN DEFAULT FALSE,
    DiabeticRetinopathySeverity VARCHAR(50) CHECK (DiabeticRetinopathySeverity IN ('None', 'Mild', 'Moderate', 'Severe', 'Proliferative')),
    
    -- Stroke Risk Assessment
    StrokeRisk VARCHAR(50) CHECK (StrokeRisk IN ('Low', 'Medium', 'High')),
    StrokeScore DECIMAL(5,2),
    
    -- Vascular Abnormalities
    VesselTortuosity DECIMAL(5,2),
    VesselWidthVariation DECIMAL(5,2),
    MicroaneurysmsCount INTEGER DEFAULT 0,
    HemorrhagesDetected BOOLEAN DEFAULT FALSE,
    ExudatesDetected BOOLEAN DEFAULT FALSE,
    
    -- Annotated Image
    AnnotatedImageUrl VARCHAR(500),
    HeatmapUrl VARCHAR(500),
    
    -- AI Confidence
    AiConfidenceScore DECIMAL(5,2) CHECK (AiConfidenceScore >= 0 AND AiConfidenceScore <= 100),
    
    -- Recommendations
    Recommendations TEXT,
    HealthWarnings TEXT,
    
    -- Processing Info
    ProcessingTimeSeconds INTEGER,
    AnalysisStartedAt TIMESTAMP,
    AnalysisCompletedAt TIMESTAMP,
    
    -- Additional Data
    DetailedFindings JSONB,
    RawAiOutput JSONB,
    
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 11. ANNOTATIONS
-- =====================================================

CREATE TABLE annotations (
    Id VARCHAR(255) PRIMARY KEY,
    ResultId VARCHAR(255) NOT NULL REFERENCES analysis_results(Id) ON DELETE CASCADE,
    AnnotationType VARCHAR(50) NOT NULL CHECK (AnnotationType IN ('Vessel', 'Microaneurysm', 'Hemorrhage', 'Exudate', 'Abnormality', 'Other')),
    Coordinates JSONB NOT NULL,
    Description TEXT,
    Severity VARCHAR(50),
    ConfidenceScore DECIMAL(5,2),
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 12. AI FEEDBACK
-- =====================================================

CREATE TABLE ai_feedback (
    Id VARCHAR(255) PRIMARY KEY,
    ResultId VARCHAR(255) NOT NULL REFERENCES analysis_results(Id) ON DELETE CASCADE,
    DoctorId VARCHAR(255) NOT NULL REFERENCES doctors(Id) ON DELETE CASCADE,
    FeedbackType VARCHAR(50) NOT NULL CHECK (FeedbackType IN ('Correct', 'Incorrect', 'PartiallyCorrect', 'NeedsReview')),
    OriginalRiskLevel VARCHAR(50),
    CorrectedRiskLevel VARCHAR(50),
    FeedbackNotes TEXT,
    IsUsedForTraining BOOLEAN DEFAULT FALSE,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 13. SERVICE PACKAGES
-- =====================================================

CREATE TABLE service_packages (
    Id VARCHAR(255) PRIMARY KEY,
    PackageName VARCHAR(255) NOT NULL,
    PackageType VARCHAR(50) NOT NULL CHECK (PackageType IN ('Individual', 'Clinic', 'Enterprise')),
    Description TEXT,
    NumberOfAnalyses INTEGER NOT NULL,
    Price DECIMAL(10,2) NOT NULL,
    Currency VARCHAR(10) DEFAULT 'VND',
    ValidityDays INTEGER,
    Features TEXT,
    IsActive BOOLEAN DEFAULT TRUE,
    CreatedBy VARCHAR(255) REFERENCES admins(Id),
    CreatedDate DATE,
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 14. USER PACKAGES (PURCHASED PACKAGES)
-- =====================================================

CREATE TABLE user_packages (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) REFERENCES users(Id) ON DELETE CASCADE,
    ClinicId VARCHAR(255) REFERENCES clinics(Id) ON DELETE CASCADE,
    PackageId VARCHAR(255) NOT NULL REFERENCES service_packages(Id),
    TotalAnalyses INTEGER NOT NULL DEFAULT 0,
    UsedAnalyses INTEGER NOT NULL DEFAULT 0,
    RemainingAnalyses INTEGER NOT NULL,
    PurchasedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ExpiresAt TIMESTAMP,
    IsActive BOOLEAN DEFAULT TRUE,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    CHECK ((UserId IS NOT NULL AND ClinicId IS NULL) OR (UserId IS NULL AND ClinicId IS NOT NULL))
);

-- =====================================================
-- 15. PAYMENT HISTORY
-- =====================================================

CREATE TABLE payment_history (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) REFERENCES users(Id) ON DELETE SET NULL,
    ClinicId VARCHAR(255) REFERENCES clinics(Id) ON DELETE SET NULL,
    PackageId VARCHAR(255) NOT NULL REFERENCES service_packages(Id),
    UserPackageId VARCHAR(255) REFERENCES user_packages(Id) ON DELETE SET NULL,
    PaymentMethod VARCHAR(50) CHECK (PaymentMethod IN ('CreditCard', 'DebitCard', 'BankTransfer', 'E-Wallet', 'Other')),
    PaymentProvider VARCHAR(100),
    TransactionId VARCHAR(255) UNIQUE,
    Amount DECIMAL(10,2) NOT NULL,
    Currency VARCHAR(10) DEFAULT 'VND',
    PaymentStatus VARCHAR(50) DEFAULT 'Pending' CHECK (PaymentStatus IN ('Pending', 'Completed', 'Failed', 'Refunded')),
    PaymentDate TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ReceiptUrl VARCHAR(500),
    Notes TEXT,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    CHECK ((UserId IS NOT NULL AND ClinicId IS NULL) OR (UserId IS NULL AND ClinicId IS NOT NULL))
);

-- =====================================================
-- 16. MESSAGES (CONSULTATION CHAT)
-- =====================================================

CREATE TABLE messages (
    Id VARCHAR(255) PRIMARY KEY,
    ConversationId VARCHAR(255) NOT NULL,
    SendById VARCHAR(255) NOT NULL,
    SendByType VARCHAR(20) NOT NULL CHECK (SendByType IN ('User', 'Doctor', 'Admin')),
    ReceiverId VARCHAR(255) NOT NULL,
    ReceiverType VARCHAR(20) NOT NULL CHECK (ReceiverType IN ('User', 'Doctor', 'Admin')),
    Subject VARCHAR(255),
    Content TEXT NOT NULL,
    AttachmentUrl VARCHAR(500),
    IsRead BOOLEAN DEFAULT FALSE,
    ReadAt TIMESTAMP,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 17. NOTIFICATIONS
-- =====================================================

CREATE TABLE notifications (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) REFERENCES users(Id) ON DELETE CASCADE,
    DoctorId VARCHAR(255) REFERENCES doctors(Id) ON DELETE CASCADE,
    ClinicId VARCHAR(255) REFERENCES clinics(Id) ON DELETE CASCADE,
    AdminId VARCHAR(255) REFERENCES admins(Id) ON DELETE CASCADE,
    NotificationType VARCHAR(50) NOT NULL CHECK (NotificationType IN ('AnalysisComplete', 'HighRiskAlert', 'PaymentSuccess', 'PackageExpiring', 'MessageReceived', 'SystemAlert', 'Other')),
    Title VARCHAR(255) NOT NULL,
    Description TEXT NOT NULL,
    RelatedEntityType VARCHAR(50),
    RelatedEntityId VARCHAR(255),
    IsRead BOOLEAN DEFAULT FALSE,
    ReadAt TIMESTAMP,
    CreatedDate TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255),
    CHECK (
        (UserId IS NOT NULL AND DoctorId IS NULL AND ClinicId IS NULL AND AdminId IS NULL) OR
        (UserId IS NULL AND DoctorId IS NOT NULL AND ClinicId IS NULL AND AdminId IS NULL) OR
        (UserId IS NULL AND DoctorId IS NULL AND ClinicId IS NOT NULL AND AdminId IS NULL) OR
        (UserId IS NULL AND DoctorId IS NULL AND ClinicId IS NULL AND AdminId IS NOT NULL)
    )
);

-- =====================================================
-- 18. AUDIT LOGS
-- =====================================================

CREATE TABLE audit_logs (
    Id VARCHAR(255) PRIMARY KEY,
    UserId VARCHAR(255) REFERENCES users(Id) ON DELETE SET NULL,
    DoctorId VARCHAR(255) REFERENCES doctors(Id) ON DELETE SET NULL,
    AdminId VARCHAR(255) REFERENCES admins(Id) ON DELETE SET NULL,
    ActionType VARCHAR(100) NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    ResourceId VARCHAR(255),
    OldValues JSONB,
    NewValues JSONB,
    IpAddress VARCHAR(45),
    UserAgent TEXT,
    CreatedDate DATE,
    CreatedBy VARCHAR(255)
);

-- =====================================================
-- 19. MEDICAL NOTES (FR-16: Doctor add notes/diagnoses)
-- =====================================================

CREATE TABLE medical_notes (
    Id VARCHAR(255) PRIMARY KEY,
    ResultId VARCHAR(255) REFERENCES analysis_results(Id) ON DELETE SET NULL,
    PatientUserId VARCHAR(255) REFERENCES users(Id) ON DELETE CASCADE,
    DoctorId VARCHAR(255) NOT NULL REFERENCES doctors(Id) ON DELETE CASCADE,
    NoteType VARCHAR(50) NOT NULL CHECK (NoteType IN ('Diagnosis', 'Recommendation', 'FollowUp', 'General', 'Prescription', 'Treatment', 'Observation', 'Referral', 'Other')),
    NoteContent TEXT NOT NULL,
    Diagnosis VARCHAR(500),
    Prescription TEXT,
    TreatmentPlan TEXT,
    ClinicalObservations TEXT,
    Severity VARCHAR(50) CHECK (Severity IN ('Low', 'Medium', 'High', 'Critical')),
    FollowUpDate TIMESTAMP,
    IsImportant BOOLEAN DEFAULT FALSE,
    IsPrivate BOOLEAN DEFAULT FALSE,
    ViewedByPatientAt TIMESTAMP,
    CreatedDate TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CreatedBy VARCHAR(255),
    UpdatedDate TIMESTAMP,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE
);

-- =====================================================
-- 20. EXPORTED REPORTS (FR-7: Download/Export PDF/CSV)
-- =====================================================

CREATE TABLE exported_reports (
    Id VARCHAR(255) PRIMARY KEY,
    ResultId VARCHAR(255) REFERENCES analysis_results(Id) ON DELETE SET NULL,
    ReportType VARCHAR(50) NOT NULL CHECK (ReportType IN ('PDF', 'CSV', 'JSON', 'Excel')),
    FilePath VARCHAR(500) NOT NULL,
    FileUrl VARCHAR(500),
    FileSize BIGINT,
    RequestedBy VARCHAR(255) NOT NULL,
    RequestedByType VARCHAR(20) NOT NULL CHECK (RequestedByType IN ('User', 'Doctor', 'Admin', 'Clinic')),
    ExportedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ExpiresAt TIMESTAMP,
    DownloadCount INTEGER DEFAULT 0,
    LastDownloadedAt TIMESTAMP,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE
);

-- =====================================================
-- 21. CLINIC REPORTS (FR-26: Generate clinic-wide reports)
-- =====================================================

CREATE TABLE clinic_reports (
    Id VARCHAR(255) PRIMARY KEY,
    ClinicId VARCHAR(255) NOT NULL REFERENCES clinics(Id) ON DELETE CASCADE,
    ReportName VARCHAR(255) NOT NULL,
    ReportType VARCHAR(50) NOT NULL CHECK (ReportType IN ('ScreeningCampaign', 'RiskDistribution', 'MonthlySummary', 'AnnualReport', 'Custom')),
    PeriodStart DATE,
    PeriodEnd DATE,
    TotalPatients INTEGER DEFAULT 0,
    TotalAnalyses INTEGER DEFAULT 0,
    HighRiskCount INTEGER DEFAULT 0,
    MediumRiskCount INTEGER DEFAULT 0,
    LowRiskCount INTEGER DEFAULT 0,
    ReportData JSONB,
    GeneratedBy VARCHAR(255) REFERENCES admins(Id),
    GeneratedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ReportFileUrl VARCHAR(500),
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE
);

-- =====================================================
-- 22. AI CONFIGURATIONS (FR-33: Configure AI parameters)
-- =====================================================

CREATE TABLE ai_configurations (
    Id VARCHAR(255) PRIMARY KEY,
    ConfigurationName VARCHAR(255) NOT NULL UNIQUE,
    ConfigurationType VARCHAR(50) NOT NULL CHECK (ConfigurationType IN ('Threshold', 'Parameter', 'Policy', 'Retraining')),
    ModelVersionId VARCHAR(255) REFERENCES ai_model_versions(Id),
    ParameterKey VARCHAR(255) NOT NULL,
    ParameterValue TEXT NOT NULL,
    ParameterDataType VARCHAR(50) CHECK (ParameterDataType IN ('Number', 'String', 'Boolean', 'JSON')),
    Description TEXT,
    IsActive BOOLEAN DEFAULT TRUE,
    AppliedAt TIMESTAMP,
    AppliedBy VARCHAR(255) REFERENCES admins(Id),
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 23. NOTIFICATION TEMPLATES (FR-39: Manage templates)
-- =====================================================

CREATE TABLE notification_templates (
    Id VARCHAR(255) PRIMARY KEY,
    TemplateName VARCHAR(255) NOT NULL UNIQUE,
    TemplateType VARCHAR(50) NOT NULL CHECK (TemplateType IN ('AnalysisComplete', 'HighRiskAlert', 'PaymentSuccess', 'PackageExpiring', 'MessageReceived', 'SystemAlert', 'Custom')),
    TitleTemplate VARCHAR(500) NOT NULL,
    ContentTemplate TEXT NOT NULL,
    Variables JSONB,
    IsActive BOOLEAN DEFAULT TRUE,
    Language VARCHAR(10) DEFAULT 'vi' CHECK (Language IN ('vi', 'en')),
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- =====================================================
-- 24. BULK UPLOAD BATCHES (FR-24: Bulk upload tracking)
-- =====================================================

CREATE TABLE bulk_upload_batches (
    Id VARCHAR(255) PRIMARY KEY,
    ClinicId VARCHAR(255) NOT NULL REFERENCES clinics(Id) ON DELETE CASCADE,
    UploadedBy VARCHAR(255) NOT NULL,
    UploadedByType VARCHAR(20) NOT NULL CHECK (UploadedByType IN ('Doctor', 'Admin', 'ClinicManager')),
    BatchName VARCHAR(255),
    TotalImages INTEGER NOT NULL DEFAULT 0,
    ProcessedImages INTEGER DEFAULT 0,
    FailedImages INTEGER DEFAULT 0,
    ProcessingImages INTEGER DEFAULT 0,
    UploadStatus VARCHAR(50) DEFAULT 'Pending' CHECK (UploadStatus IN ('Pending', 'Uploading', 'Processing', 'Completed', 'Failed', 'PartiallyCompleted')),
    StartedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CompletedAt TIMESTAMP,
    FailureReason TEXT,
    Metadata JSONB,
    CreatedDate DATE,
    CreatedBy VARCHAR(255),
    UpdatedDate DATE,
    UpdatedBy VARCHAR(255),
    IsDeleted BOOLEAN DEFAULT FALSE,
    Note VARCHAR(255)
);

-- Add BatchId to retinal_images for bulk upload tracking
ALTER TABLE retinal_images ADD COLUMN IF NOT EXISTS BatchId VARCHAR(255) REFERENCES bulk_upload_batches(Id) ON DELETE SET NULL;

-- =====================================================
-- 25. ANALYSIS JOBS (clinic batch AI analysis tracking)
-- =====================================================
CREATE TABLE IF NOT EXISTS analysis_jobs (
    Id VARCHAR(255) PRIMARY KEY,
    BatchId VARCHAR(255) NOT NULL,
    ClinicId VARCHAR(255) REFERENCES clinics(Id) ON DELETE CASCADE,
    Status VARCHAR(50) DEFAULT 'Queued' CHECK (Status IN ('Queued', 'Processing', 'Completed', 'Failed')),
    TotalImages INTEGER NOT NULL,
    ProcessedCount INTEGER DEFAULT 0,
    SuccessCount INTEGER DEFAULT 0,
    FailedCount INTEGER DEFAULT 0,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    StartedAt TIMESTAMP,
    CompletedAt TIMESTAMP,
    ImageIds JSONB,
    ErrorMessage TEXT,
    CreatedDate DATE DEFAULT CURRENT_DATE,
    IsDeleted BOOLEAN DEFAULT FALSE
);
CREATE INDEX IF NOT EXISTS idx_analysis_jobs_clinic_id ON analysis_jobs(ClinicId);
CREATE INDEX IF NOT EXISTS idx_analysis_jobs_created_at ON analysis_jobs(CreatedAt DESC);

-- Clinic support: allow analysis_results.UserId to store clinic Id (phòng khám dùng UserId = ClinicId khi tạo phân tích)
-- Nếu không drop FK này, INSERT analysis_results với UserId = clinicId sẽ lỗi vì clinicId không có trong users(Id)
ALTER TABLE analysis_results DROP CONSTRAINT IF EXISTS analysis_results_userid_fkey;

-- Clinic support: allow retinal_images.UserId to be placeholder Guid khi upload từ clinic (không có patientUserId)
ALTER TABLE retinal_images DROP CONSTRAINT IF EXISTS retinal_images_userid_fkey;

-- Add ViewedByPatientAt for patient "đã xem" badge (chạy nếu DB đã tồn tại trước khi thêm cột)
ALTER TABLE medical_notes ADD COLUMN IF NOT EXISTS ViewedByPatientAt TIMESTAMP NULL;

-- =====================================================
-- INDEXES FOR PERFORMANCE
-- =====================================================

-- Users indexes
CREATE INDEX idx_users_email ON users(Email);
CREATE INDEX idx_users_provider_user_id ON users(ProviderUserId);
CREATE INDEX idx_users_is_active ON users(IsActive);
CREATE INDEX idx_users_is_deleted ON users(IsDeleted);

-- Doctors indexes
CREATE INDEX idx_doctors_email ON doctors(Email);
CREATE INDEX idx_doctors_license_number ON doctors(LicenseNumber);
CREATE INDEX idx_doctors_is_verified ON doctors(IsVerified);
CREATE INDEX idx_doctors_is_deleted ON doctors(IsDeleted);

-- Clinics indexes
CREATE INDEX idx_clinics_email ON clinics(Email);
CREATE INDEX idx_clinics_verification_status ON clinics(VerificationStatus);
CREATE INDEX idx_clinics_is_active ON clinics(IsActive);
CREATE INDEX idx_clinics_is_deleted ON clinics(IsDeleted);

-- Admins indexes
CREATE INDEX idx_admins_email ON admins(Email);
CREATE INDEX idx_admins_role_id ON admins(RoleId);
CREATE INDEX idx_admins_is_deleted ON admins(IsDeleted);

-- Retinal Images indexes
CREATE INDEX idx_retinal_images_user_id ON retinal_images(UserId);
CREATE INDEX idx_retinal_images_clinic_id ON retinal_images(ClinicId);
CREATE INDEX idx_retinal_images_doctor_id ON retinal_images(DoctorId);
CREATE INDEX idx_retinal_images_upload_status ON retinal_images(UploadStatus);
CREATE INDEX idx_retinal_images_uploaded_at ON retinal_images(UploadedAt);
CREATE INDEX idx_retinal_images_is_deleted ON retinal_images(IsDeleted);

-- Analysis Results indexes
CREATE INDEX idx_analysis_results_image_id ON analysis_results(ImageId);
CREATE INDEX idx_analysis_results_user_id ON analysis_results(UserId);
CREATE INDEX idx_analysis_results_risk_level ON analysis_results(OverallRiskLevel);
CREATE INDEX idx_analysis_results_status ON analysis_results(AnalysisStatus);
CREATE INDEX idx_analysis_results_is_deleted ON analysis_results(IsDeleted);

-- Annotations indexes
CREATE INDEX idx_annotations_result_id ON annotations(ResultId);
CREATE INDEX idx_annotations_type ON annotations(AnnotationType);
CREATE INDEX idx_annotations_is_deleted ON annotations(IsDeleted);

-- Messages indexes
CREATE INDEX idx_messages_conversation_id ON messages(ConversationId);
CREATE INDEX idx_messages_send_by ON messages(SendById, SendByType);
CREATE INDEX idx_messages_receiver ON messages(ReceiverId, ReceiverType);
CREATE INDEX idx_messages_is_read ON messages(IsRead);
CREATE INDEX idx_messages_is_deleted ON messages(IsDeleted);

-- Notifications indexes
CREATE INDEX idx_notifications_user_id ON notifications(UserId);
CREATE INDEX idx_notifications_doctor_id ON notifications(DoctorId);
CREATE INDEX idx_notifications_clinic_id ON notifications(ClinicId);
CREATE INDEX idx_notifications_is_read ON notifications(IsRead);
CREATE INDEX idx_notifications_is_deleted ON notifications(IsDeleted);

-- Payment History indexes
CREATE INDEX idx_payment_history_user_id ON payment_history(UserId);
CREATE INDEX idx_payment_history_clinic_id ON payment_history(ClinicId);
CREATE INDEX idx_payment_history_status ON payment_history(PaymentStatus);
CREATE INDEX idx_payment_history_date ON payment_history(PaymentDate);
CREATE INDEX idx_payment_history_is_deleted ON payment_history(IsDeleted);

-- User Packages indexes
CREATE INDEX idx_user_packages_user_id ON user_packages(UserId);
CREATE INDEX idx_user_packages_clinic_id ON user_packages(ClinicId);
CREATE INDEX idx_user_packages_is_active ON user_packages(IsActive);
CREATE INDEX idx_user_packages_expires_at ON user_packages(ExpiresAt);
CREATE INDEX idx_user_packages_is_deleted ON user_packages(IsDeleted);

-- Audit Logs indexes
CREATE INDEX idx_audit_logs_user_id ON audit_logs(UserId);
CREATE INDEX idx_audit_logs_action_type ON audit_logs(ActionType);
CREATE INDEX idx_audit_logs_resource_type ON audit_logs(ResourceType);
CREATE INDEX idx_audit_logs_created_date ON audit_logs(CreatedDate);
CREATE INDEX idx_audit_logs_doctor_id ON audit_logs(DoctorId);
CREATE INDEX idx_audit_logs_admin_id ON audit_logs(AdminId);

-- Clinic relationships indexes
CREATE INDEX idx_clinic_doctors_clinic_id ON clinic_doctors(ClinicId);
CREATE INDEX idx_clinic_doctors_doctor_id ON clinic_doctors(DoctorId);
CREATE INDEX idx_clinic_users_clinic_id ON clinic_users(ClinicId);
CREATE INDEX idx_clinic_users_user_id ON clinic_users(UserId);
CREATE INDEX idx_patient_doctor_assignments_user_id ON patient_doctor_assignments(UserId);
CREATE INDEX idx_patient_doctor_assignments_doctor_id ON patient_doctor_assignments(DoctorId);

-- User Roles indexes
CREATE INDEX idx_user_roles_user_id ON user_roles(UserId);
CREATE INDEX idx_user_roles_role_id ON user_roles(RoleId);
CREATE INDEX idx_user_roles_is_deleted ON user_roles(IsDeleted);

-- Medical Notes indexes
CREATE INDEX idx_medical_notes_result_id ON medical_notes(ResultId);
CREATE INDEX idx_medical_notes_patient_user_id ON medical_notes(PatientUserId);
CREATE INDEX idx_medical_notes_doctor_id ON medical_notes(DoctorId);
CREATE INDEX idx_medical_notes_note_type ON medical_notes(NoteType);
CREATE INDEX idx_medical_notes_is_deleted ON medical_notes(IsDeleted);
CREATE INDEX idx_medical_notes_is_private ON medical_notes(IsPrivate);

-- Exported Reports indexes
CREATE INDEX idx_exported_reports_result_id ON exported_reports(ResultId);
CREATE INDEX idx_exported_reports_report_type ON exported_reports(ReportType);
CREATE INDEX idx_exported_reports_exported_at ON exported_reports(ExportedAt);
CREATE INDEX idx_exported_reports_is_deleted ON exported_reports(IsDeleted);

-- Clinic Reports indexes
CREATE INDEX idx_clinic_reports_clinic_id ON clinic_reports(ClinicId);
CREATE INDEX idx_clinic_reports_report_type ON clinic_reports(ReportType);
CREATE INDEX idx_clinic_reports_period ON clinic_reports(PeriodStart, PeriodEnd);
CREATE INDEX idx_clinic_reports_generated_at ON clinic_reports(GeneratedAt);
CREATE INDEX idx_clinic_reports_is_deleted ON clinic_reports(IsDeleted);

-- AI Configurations indexes
CREATE INDEX idx_ai_configurations_config_type ON ai_configurations(ConfigurationType);
CREATE INDEX idx_ai_configurations_model_version_id ON ai_configurations(ModelVersionId);
CREATE INDEX idx_ai_configurations_is_active ON ai_configurations(IsActive);
CREATE INDEX idx_ai_configurations_is_deleted ON ai_configurations(IsDeleted);

-- Notification Templates indexes
CREATE INDEX idx_notification_templates_template_type ON notification_templates(TemplateType);
CREATE INDEX idx_notification_templates_is_active ON notification_templates(IsActive);
CREATE INDEX idx_notification_templates_language ON notification_templates(Language);
CREATE INDEX idx_notification_templates_is_deleted ON notification_templates(IsDeleted);

-- Bulk Upload Batches indexes
CREATE INDEX idx_bulk_upload_batches_clinic_id ON bulk_upload_batches(ClinicId);
CREATE INDEX idx_bulk_upload_batches_upload_status ON bulk_upload_batches(UploadStatus);
CREATE INDEX idx_bulk_upload_batches_started_at ON bulk_upload_batches(StartedAt);
CREATE INDEX idx_bulk_upload_batches_is_deleted ON bulk_upload_batches(IsDeleted);

-- Retinal Images batch index
CREATE INDEX idx_retinal_images_batch_id ON retinal_images(BatchId);

-- =====================================================
-- COMMENTS ON TABLES
-- =====================================================

COMMENT ON TABLE users IS 'Stores patient/user account information';
COMMENT ON TABLE doctors IS 'Stores doctor account information and credentials';
COMMENT ON TABLE clinics IS 'Stores clinic/organization information';
COMMENT ON TABLE admins IS 'Stores system administrator accounts';
COMMENT ON TABLE retinal_images IS 'Stores uploaded retinal fundus/OCT images';
COMMENT ON TABLE analysis_results IS 'Stores AI-generated analysis results and risk assessments';
COMMENT ON TABLE annotations IS 'Stores detailed annotations of detected abnormalities';
COMMENT ON TABLE service_packages IS 'Stores available service packages for purchase';
COMMENT ON TABLE user_packages IS 'Stores purchased packages and remaining analysis credits';
COMMENT ON TABLE payment_history IS 'Stores all payment transactions';
COMMENT ON TABLE messages IS 'Stores consultation chat messages between users and doctors';
COMMENT ON TABLE notifications IS 'Stores system notifications for all user types';
COMMENT ON TABLE audit_logs IS 'Stores audit trail for system actions and changes';
COMMENT ON TABLE user_roles IS 'Junction table linking users to their roles';
COMMENT ON TABLE medical_notes IS 'Stores medical notes, diagnoses, and recommendations from doctors (FR-16)';
COMMENT ON TABLE exported_reports IS 'Stores history of exported reports in PDF/CSV format (FR-7)';
COMMENT ON TABLE clinic_reports IS 'Stores clinic-wide reports for screening campaigns (FR-26, FR-30)';
COMMENT ON TABLE ai_configurations IS 'Stores AI parameters, thresholds, and retraining policies (FR-33)';
COMMENT ON TABLE notification_templates IS 'Stores notification message templates (FR-39)';
COMMENT ON TABLE bulk_upload_batches IS 'Tracks bulk image upload batches from clinics (FR-24)';

-- =====================================================
-- SEED DATA: DEFAULT ADMIN ACCOUNT
-- =====================================================

-- Insert default admin role
INSERT INTO roles (Id, RoleName, CreatedDate, IsDeleted, Note)
VALUES ('admin-role-001', 'Admin', CURRENT_DATE, false, 'Quản trị viên hệ thống')
ON CONFLICT (Id) DO NOTHING;

-- Insert default super admin role
INSERT INTO roles (Id, RoleName, CreatedDate, IsDeleted, Note)
VALUES ('superadmin-role-001', 'SuperAdmin', CURRENT_DATE, false, 'Quản trị viên cấp cao')
ON CONFLICT (Id) DO NOTHING;

-- Insert Doctor role (required for doctor registration and role assignment)
INSERT INTO roles (Id, RoleName, CreatedDate, IsDeleted, Note)
VALUES ('doctor-role-001', 'Doctor', CURRENT_DATE, false, 'Bác sĩ - có thể quản lý bệnh nhân và phân tích hình ảnh')
ON CONFLICT (Id) DO NOTHING;

-- Insert Patient role (for regular users)
INSERT INTO roles (Id, RoleName, CreatedDate, IsDeleted, Note)
VALUES ('patient-role-001', 'Patient', CURRENT_DATE, false, 'Bệnh nhân - người dùng thông thường của hệ thống')
ON CONFLICT (Id) DO NOTHING;

-- Insert Clinic role (for clinic management)
INSERT INTO roles (Id, RoleName, CreatedDate, IsDeleted, Note)
VALUES ('clinic-role-001', 'Clinic', CURRENT_DATE, false, 'Phòng khám - quản lý bác sĩ và bệnh nhân')
ON CONFLICT (Id) DO NOTHING;

-- Insert default admin account
-- Email: admin@aura.com
-- Password: 123456 (hashed using SHA256 + Base64)
-- Hash: jZae727K08KaOmKSgOaGzww/XVqGr/PKEgIMkjrcbJI=
INSERT INTO admins (
    Id, 
    Username, 
    Password, 
    FirstName, 
    LastName, 
    Email, 
    RoleId, 
    IsSuperAdmin, 
    IsActive, 
    CreatedDate
)
VALUES (
    'admin-001',
    'admin',
    'jZae727K08KaOmKSgOaGzww/XVqGr/PKEgIMkjrcbJI=',  -- SHA256 hash of "123456"
    'Admin',
    'AURA',
    'admin@aura.com',
    'superadmin-role-001',
    true,
    true,
    CURRENT_DATE
)
ON CONFLICT (Email) DO NOTHING;

-- =====================================================
-- SEED DATA: SERVICE PACKAGES
-- =====================================================

-- Insert sample service packages for testing and development
INSERT INTO service_packages (Id, PackageName, PackageType, Description, NumberOfAnalyses, Price, Currency, ValidityDays, IsActive, CreatedDate, IsDeleted)
VALUES 
    -- Individual Packages
    ('pkg-individual-basic', 'Gói Cơ Bản', 'Individual', 'Gói phân tích cơ bản cho cá nhân - 10 lần phân tích', 10, 500000, 'VND', 30, true, CURRENT_DATE, false),
    ('pkg-individual-standard', 'Gói Tiêu Chuẩn', 'Individual', 'Gói phân tích tiêu chuẩn cho cá nhân - 25 lần phân tích', 25, 1000000, 'VND', 60, true, CURRENT_DATE, false),
    ('pkg-individual-premium', 'Gói Cao Cấp', 'Individual', 'Gói phân tích cao cấp cho cá nhân - 50 lần phân tích', 50, 1800000, 'VND', 90, true, CURRENT_DATE, false),
    
    -- Clinic Packages
    ('pkg-clinic-starter', 'Gói Khởi Động Phòng Khám', 'Clinic', 'Gói cho phòng khám mới - 100 lần phân tích', 100, 8000000, 'VND', 90, true, CURRENT_DATE, false),
    ('pkg-clinic-professional', 'Gói Chuyên Nghiệp', 'Clinic', 'Gói chuyên nghiệp cho phòng khám - 250 lần phân tích', 250, 18000000, 'VND', 180, true, CURRENT_DATE, false),
    ('pkg-clinic-enterprise', 'Gói Doanh Nghiệp', 'Clinic', 'Gói doanh nghiệp cho phòng khám lớn - 500 lần phân tích', 500, 32000000, 'VND', 365, true, CURRENT_DATE, false),
    
    -- Enterprise Packages
    ('pkg-enterprise-basic', 'Gói Doanh Nghiệp Cơ Bản', 'Enterprise', 'Gói cơ bản cho doanh nghiệp - 1000 lần phân tích', 1000, 60000000, 'VND', 365, true, CURRENT_DATE, false),
    ('pkg-enterprise-advanced', 'Gói Doanh Nghiệp Nâng Cao', 'Enterprise', 'Gói nâng cao cho doanh nghiệp - 2500 lần phân tích', 2500, 99999999, 'VND', 365, true, CURRENT_DATE, false)
ON CONFLICT (Id) DO UPDATE SET
    PackageName = EXCLUDED.PackageName,
    PackageType = EXCLUDED.PackageType,
    Description = EXCLUDED.Description,
    NumberOfAnalyses = EXCLUDED.NumberOfAnalyses,
    Price = EXCLUDED.Price,
    Currency = EXCLUDED.Currency,
    ValidityDays = EXCLUDED.ValidityDays,
    IsActive = EXCLUDED.IsActive,
    UpdatedDate = CURRENT_DATE;

-- =====================================================
-- END OF SCHEMA
-- =====================================================