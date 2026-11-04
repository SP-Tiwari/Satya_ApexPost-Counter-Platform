namespace ApexPost.Domain.Specifications;

// A reusable, combinable filter for report and dashboard queries. Restricting
// T to reference types keeps specifications to entities and records, which
// the reports filter by identity and state.
public interface ISpecification<in T>
	where T : class
{
	bool IsSatisfiedBy(T candidate);
}

public abstract class Specification<T> : ISpecification<T>
	where T : class
{
	public abstract bool IsSatisfiedBy(T candidate);

	public Specification<T> And(ISpecification<T> other) =>
		new PredicateSpecification<T>(candidate =>
			IsSatisfiedBy(candidate) && other.IsSatisfiedBy(candidate));
}

public sealed class PredicateSpecification<T>(Predicate<T> predicate)
	: Specification<T>
	where T : class
{
	private readonly Predicate<T> _predicate =
		predicate ?? throw new ArgumentNullException(nameof(predicate));

	public override bool IsSatisfiedBy(T candidate)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		return _predicate(candidate);
	}
}

public static class SpecificationExtensions
{
	extension<T>(IEnumerable<T> source)
		where T : class
	{
		public IEnumerable<T> Matching(ISpecification<T> specification)
		{
			ArgumentNullException.ThrowIfNull(specification);
			return source.Where(specification.IsSatisfiedBy);
		}
	}
}
