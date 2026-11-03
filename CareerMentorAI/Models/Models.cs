using System.Text.Json;

namespace CareerMentorAI;

public record ChatMessage(string Role, string Content);
public record ChatRequest(List<ChatMessage> Messages);

public record PlanRequest(
    JsonElement Analysis,
    int HoursPerWeek,
    int HorizonMonths,
    string? FocusArea
);

public record CoachStreamRequest(
    List<ChatMessage> Messages,
    JsonElement? Analysis,
    JsonElement? Plan,
    JsonElement? Extras,
    int? GoalsLimit
);