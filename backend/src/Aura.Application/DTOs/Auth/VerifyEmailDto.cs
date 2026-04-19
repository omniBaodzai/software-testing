using System.ComponentModel.DataAnnotations;

namespace Aura.Application.DTOs.Auth;

public class VerifyEmailDto
{
    [Required(ErrorMessage = "Token không được để trống")]
    public string Token { get; set; } = string.Empty;
}

public class ResendVerificationEmailDto
{
    [Required(ErrorMessage = "Email không được để trống")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng")]
    public string Email { get; set; } = string.Empty;
}