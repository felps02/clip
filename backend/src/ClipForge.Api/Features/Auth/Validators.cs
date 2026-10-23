using FluentValidation;

namespace ClipForge.Api.Features.Auth;

public static class PasswordRules
{
    public const string Pattern = @"^(?=.*[A-Za-z])(?=.*\d).{8,128}$";
    public const string Message = "A senha deve ter pelo menos 8 caracteres, incluindo uma letra e um número.";

    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().WithMessage("Informe a senha.").Matches(Pattern).WithMessage(Message);
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.").EmailAddress().WithMessage("E-mail inválido.").MaximumLength(254);
        RuleFor(x => x.Password).Password();
        RuleFor(x => x.DisplayName).NotEmpty().WithMessage("Informe o nome.").MinimumLength(2).MaximumLength(80);
        RuleFor(x => x.AcceptedTerms).Equal(true).WithMessage("É necessário aceitar os termos de uso.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.").MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Informe a senha.").MaximumLength(128);
    }
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.").EmailAddress().WithMessage("E-mail inválido.");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("E-mail inválido.");
        RuleFor(x => x.Token).NotEmpty().WithMessage("Token inválido.");
        RuleFor(x => x.NewPassword).Password();
    }
}

public sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Token).NotEmpty().WithMessage("Token inválido.");
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Informe a senha atual.");
        RuleFor(x => x.NewPassword).Password();
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("A nova senha deve ser diferente da atual.");
    }
}
