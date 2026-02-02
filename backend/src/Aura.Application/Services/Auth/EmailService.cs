using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aura.Application.Services.Auth;

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private readonly string _frontendUrl;
    private readonly string? _smtpHost;
    private readonly int _smtpPort;
    private readonly string? _smtpUsername;
    private readonly string? _smtpPassword;
    private readonly string _fromEmail;
    private readonly string _fromName;
    private readonly bool _enableSmtp;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _frontendUrl = _configuration["App:FrontendUrl"] ?? "http://localhost:5173";
        
        // Email configuration
        _smtpHost = _configuration["Email:SmtpHost"];
        _smtpPort = int.TryParse(_configuration["Email:SmtpPort"], out var port) ? port : 587;
        _smtpUsername = _configuration["Email:SmtpUsername"];
        _smtpPassword = _configuration["Email:SmtpPassword"];
        _fromEmail = _configuration["Email:FromEmail"] ?? "noreply@aura-health.com";
        _fromName = _configuration["Email:FromName"] ?? "AURA Health System";
        
        // Enable SMTP only if credentials are provided
        _enableSmtp = !string.IsNullOrWhiteSpace(_smtpHost) 
                   && !string.IsNullOrWhiteSpace(_smtpUsername) 
                   && !string.IsNullOrWhiteSpace(_smtpPassword);
        
        if (!_enableSmtp)
        {
            _logger.LogWarning("SMTP not configured. Email sending will be logged only. Configure Email:SmtpHost, Email:SmtpUsername, and Email:SmtpPassword to enable.");
        }
    }

    public async Task<bool> SendVerificationEmailAsync(string email, string token, string? firstName = null)
    {
        try
        {
            var verificationUrl = $"{_frontendUrl}/verify-email?token={token}";
            var subject = "Xác thực Email - AURA";
            var body = GenerateVerificationEmailBody(firstName, verificationUrl);

            if (_enableSmtp)
            {
                return await SendEmailAsync(email, subject, body);
            }
            else
            {
                // Log only if SMTP not configured
                _logger.LogInformation("Verification email for {Email}: {Url} (SMTP not configured)", email, verificationUrl);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send verification email to {Email}", email);
            return false;
        }
    }

    public async Task<bool> SendPasswordResetEmailAsync(string email, string token, string? firstName = null)
    {
        try
        {
            var resetUrl = $"{_frontendUrl}/reset-password?token={token}";
            var subject = "Đặt lại Mật khẩu - AURA";
            var body = GeneratePasswordResetEmailBody(firstName, resetUrl);

            if (_enableSmtp)
            {
                return await SendEmailAsync(email, subject, body);
            }
            else
            {
                _logger.LogInformation("Password reset email for {Email}: {Url} (SMTP not configured)", email, resetUrl);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", email);
            return false;
        }
    }

    public async Task<bool> SendWelcomeEmailAsync(string email, string? firstName = null)
    {
        try
        {
            var subject = "Chào mừng đến với AURA!";
            var body = GenerateWelcomeEmailBody(firstName);

            if (_enableSmtp)
            {
                return await SendEmailAsync(email, subject, body);
            }
            else
            {
                _logger.LogInformation("Welcome email sent to {Email} (SMTP not configured)", email);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send welcome email to {Email}", email);
            return false;
        }
    }

    /// <summary>
    /// Gửi email tùy chỉnh, dùng cho email queue (thông báo hệ thống, báo cáo, v.v.)
    /// </summary>
    public async Task<bool> SendCustomEmailAsync(string email, string subject, string htmlBody)
    {
        try
        {
            if (_enableSmtp)
            {
                return await SendEmailAsync(email, subject, htmlBody);
            }
            else
            {
                _logger.LogInformation(
                    "Custom email (SMTP not configured) To={Email}, Subject={Subject}",
                    email, subject);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send custom email to {Email}", email);
            return false;
        }
    }

    private async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        if (!_enableSmtp || string.IsNullOrWhiteSpace(_smtpHost) || string.IsNullOrWhiteSpace(_smtpUsername))
        {
            _logger.LogWarning("Cannot send email: SMTP not configured");
            return false;
        }

        try
        {
            // Log SMTP configuration (without password) for debugging
            _logger.LogInformation("Attempting to send email via SMTP. Host: {Host}, Port: {Port}, Username: {Username}", 
                _smtpHost, _smtpPort, _smtpUsername);
            
            using var client = new SmtpClient(_smtpHost, _smtpPort)
            {
                EnableSsl = _smtpPort == 587 || _smtpPort == 465,
                UseDefaultCredentials = false, // Important: Don't use Windows credentials
                Credentials = new NetworkCredential(_smtpUsername, _smtpPassword),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 30000 // 30 seconds
            };

            using var message = new MailMessage
            {
                From = new MailAddress(_fromEmail, _fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
                Priority = MailPriority.Normal
            };

            message.To.Add(toEmail);

            await client.SendMailAsync(message);
            
            _logger.LogInformation("Email sent successfully to {Email}", toEmail);
            return true;
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "SMTP error sending email to {Email}", toEmail);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email to {Email}", toEmail);
            return false;
        }
    }

    private string GenerateVerificationEmailBody(string? firstName, string verificationUrl)
    {
        var name = string.IsNullOrEmpty(firstName) ? "bạn" : firstName;
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Xác thực Email - AURA</title>
</head>
<body style='font-family: Inter, sans-serif; background-color: #f8fafc; padding: 20px;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; padding: 40px; box-shadow: 0 4px 20px rgba(0,0,0,0.05);'>
        <div style='text-align: center; margin-bottom: 30px;'>
            <h1 style='color: #3b82f6; margin: 0;'>AURA</h1>
            <p style='color: #64748b; margin: 5px 0;'>Hệ thống Sàng lọc Sức khỏe Mạch máu Võng mạc</p>
        </div>
        <h2 style='color: #0f172a;'>Xin chào {name},</h2>
        <p style='color: #64748b; line-height: 1.6;'>
            Cảm ơn bạn đã đăng ký tài khoản AURA. Vui lòng xác thực địa chỉ email của bạn bằng cách nhấp vào nút bên dưới:
        </p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{verificationUrl}' style='background-color: #3b82f6; color: #ffffff; padding: 14px 28px; text-decoration: none; border-radius: 8px; font-weight: 600; display: inline-block;'>
                Xác thực Email
            </a>
        </div>
        <p style='color: #64748b; font-size: 14px;'>
            Link xác thực sẽ hết hạn sau 24 giờ. Nếu bạn không yêu cầu xác thực này, vui lòng bỏ qua email này.
        </p>
        <hr style='border: none; border-top: 1px solid #e2e8f0; margin: 30px 0;'>
        <p style='color: #94a3b8; font-size: 12px; text-align: center;'>
            © 2026 AURA. Tuân thủ HIPAA.
        </p>
    </div>
</body>
</html>";
    }

    private string GeneratePasswordResetEmailBody(string? firstName, string resetUrl)
    {
        var name = string.IsNullOrEmpty(firstName) ? "bạn" : firstName;
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Đặt lại Mật khẩu - AURA</title>
</head>
<body style='font-family: Inter, sans-serif; background-color: #f8fafc; padding: 20px;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; padding: 40px; box-shadow: 0 4px 20px rgba(0,0,0,0.05);'>
        <div style='text-align: center; margin-bottom: 30px;'>
            <h1 style='color: #3b82f6; margin: 0;'>AURA</h1>
            <p style='color: #64748b; margin: 5px 0;'>Hệ thống Sàng lọc Sức khỏe Mạch máu Võng mạc</p>
        </div>
        <h2 style='color: #0f172a;'>Xin chào {name},</h2>
        <p style='color: #64748b; line-height: 1.6;'>
            Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn. Nhấp vào nút bên dưới để đặt mật khẩu mới:
        </p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{resetUrl}' style='background-color: #3b82f6; color: #ffffff; padding: 14px 28px; text-decoration: none; border-radius: 8px; font-weight: 600; display: inline-block;'>
                Đặt lại Mật khẩu
            </a>
        </div>
        <p style='color: #64748b; font-size: 14px;'>
            Link đặt lại mật khẩu sẽ hết hạn sau 1 giờ. Nếu bạn không yêu cầu đặt lại mật khẩu, vui lòng bỏ qua email này.
        </p>
        <hr style='border: none; border-top: 1px solid #e2e8f0; margin: 30px 0;'>
        <p style='color: #94a3b8; font-size: 12px; text-align: center;'>
            © 2026 AURA. Tuân thủ HIPAA.
        </p>
    </div>
</body>
</html>";
    }

    private string GenerateWelcomeEmailBody(string? firstName)
    {
        var name = string.IsNullOrEmpty(firstName) ? "bạn" : firstName;
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Chào mừng - AURA</title>
</head>
<body style='font-family: Inter, sans-serif; background-color: #f8fafc; padding: 20px;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; padding: 40px; box-shadow: 0 4px 20px rgba(0,0,0,0.05);'>
        <div style='text-align: center; margin-bottom: 30px;'>
            <h1 style='color: #3b82f6; margin: 0;'>AURA</h1>
            <p style='color: #64748b; margin: 5px 0;'>Hệ thống Sàng lọc Sức khỏe Mạch máu Võng mạc</p>
        </div>
        <h2 style='color: #0f172a;'>Chào mừng {name} đến với AURA!</h2>
        <p style='color: #64748b; line-height: 1.6;'>
            Tài khoản của bạn đã được xác thực thành công. Bạn có thể bắt đầu sử dụng AURA để:
        </p>
        <ul style='color: #64748b; line-height: 1.8;'>
            <li>Tải lên hình ảnh võng mạc để phân tích</li>
            <li>Xem kết quả chẩn đoán AI</li>
            <li>Theo dõi lịch sử sức khỏe</li>
            <li>Nhận tư vấn từ bác sĩ</li>
        </ul>
        <hr style='border: none; border-top: 1px solid #e2e8f0; margin: 30px 0;'>
        <p style='color: #94a3b8; font-size: 12px; text-align: center;'>
            © 2026 AURA. Tuân thủ HIPAA.
        </p>
    </div>
</body>
</html>";
    }
}
