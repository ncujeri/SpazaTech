using FluentValidation;
using MediatR;
using SpazaHub.Application.Common.Exceptions;

namespace SpazaHub.Application.Common.Behaviors;

/// <summary>
/// MediatR pipeline step that runs every registered FluentValidation validator for the
/// request and throws AppValidationException on failure, so handlers only ever see
/// valid input.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var results = await Task.WhenAll(
                _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

            var failures = results
                .SelectMany(r => r.Errors)
                .Where(f => f is not null)
                .GroupBy(f => f.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());

            if (failures.Count > 0)
            {
                throw new AppValidationException(failures);
            }
        }

        return await next();
    }
}
