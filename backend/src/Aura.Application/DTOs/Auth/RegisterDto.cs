using System.ComponentModel.DataAnnotations;

namespace Aura.Application.DTOs.Auth;

public class RegisterDto
{
    [Required(ErrorMessage = "Email là bắt buộc")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password là bắt buộc")]
    [MinLength(6, ErrorMessage = "Password tối thiểu 6 ký tự")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Xác nhận mật khẩu là bắt buộc")]
    [Compare("Password", ErrorMessage = "Mật khẩu không khớp")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    [RegularExpression(@"^(0|\+84)[0-9]{9}$", ErrorMessage = "Số điện thoại không hợp lệ")]
    public string? Phone { get; set; }
    
    /// <summary>
    /// User type: "patient" (default) or "doctor"
    /// </summary>
    public string UserType { get; set; } = "patient";

    // Doctor-specific fields (only used when UserType = "doctor")
    public string? LicenseNumber { get; set; }
    public string? Specialization { get; set; }
    public int? YearsOfExperience { get; set; }
    public string? Qualification { get; set; }
    public string? HospitalAffiliation { get; set; }
}