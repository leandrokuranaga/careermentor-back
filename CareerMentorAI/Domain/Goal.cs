namespace CareerMentor.Api.Domain;

public class Goal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = default!;
    public string Horizon { get; set; } = "90d";
    public string Metric { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<NextAction> NextActions { get; set; } = [];
}
