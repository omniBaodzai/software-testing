using System.ComponentModel.DataAnnotations;

namespace Aura.Application.DTOs.Auth;

public class RefreshTokenDto
{
    [Required(ErrorMessage = "RefreshToken không được để trống")]
    [MinLength(10, ErrorMessage = "RefreshToken không hợp lệ")]
    public string RefreshToken { get; set; } = string.Empty;
}