using System.Text.Json.Serialization;

namespace CareerMentor.Api.Domain;

public class NextAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GoalId { get; set; }
    public string Title { get; set; } = default!;
    public int EffortHours { get; set; } = 2;
    public DateTime? DueDate { get; set; }
    
    [JsonIgnore]          

    public virtual Goal? Goal { get; set; }
}
