using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Conversation;

public sealed class ChatHistoryManager<TAssistant>
{
    private readonly int _maxChars;
    private readonly Func<TAssistant, string> _assistantFormatter;
    private readonly Func<TAssistant, int> _assistantLengthSelector;
    private readonly List<ConversationTurn> _turns = [];
    private int _storedChars;

    public ChatHistoryManager(
        int maxChars,
        Func<TAssistant, string> assistantFormatter,
        Func<TAssistant, int>? assistantLengthSelector = null)
    {
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), "History size must be greater than zero.");
        }

        this._assistantFormatter = assistantFormatter ?? throw new ArgumentNullException(nameof(assistantFormatter));
        this._assistantLengthSelector = assistantLengthSelector ?? (assistant => assistantFormatter(assistant).Length);
        this._maxChars = maxChars;
    }

    public int Push(string userRequest, TAssistant response)
    {
        var turn = new ConversationTurn(userRequest, response, this._assistantFormatter(response), this._assistantLengthSelector(response));

        this._turns.Add(turn);
        this._storedChars += turn.TotalChars;

        return this.TrimToBudget();
    }

    public IReadOnlyList<ChatMessage> BuildHistory(int availableChars)
    {
        return this.BuildHistory(availableChars, null, null);
    }

    public IReadOnlyList<ChatMessage> BuildHistory(
        int availableChars,
        Func<string, string>? userFormatter,
        Func<TAssistant, string>? assistantFormatter)
    {
        if (availableChars <= 0 || this._turns.Count == 0)
        {
            return [];
        }

        var effectiveBudget = Math.Min(availableChars, this._maxChars);
        var selectedTurns = new List<ConversationTurn>();
        var usedChars = 0;

        for (var i = this._turns.Count - 1; i >= 0; i--)
        {
            var turn = this._turns[i];
            if (usedChars + turn.TotalChars > effectiveBudget)
            {
                continue;
            }

            selectedTurns.Add(turn);
            usedChars += turn.TotalChars;
        }

        selectedTurns.Reverse();

        return selectedTurns
            .SelectMany(turn => turn.ToChatMessages(userFormatter, assistantFormatter))
            .ToArray();
    }

    private int TrimToBudget()
    {
        var removedCount = 0;

        while (this._storedChars > this._maxChars && this._turns.Count > 0)
        {
            var oldestTurn = this._turns[0];
            this._turns.RemoveAt(0);
            this._storedChars -= oldestTurn.TotalChars;
            removedCount++;
        }

        return removedCount;
    }

    private sealed record ConversationTurn(string UserRequest, TAssistant Response, string AssistantText, int AssistantLength)
    {
        public int TotalChars => this.UserRequest.Length + this.AssistantLength;

        public IReadOnlyList<ChatMessage> ToChatMessages(
            Func<string, string>? userFormatter,
            Func<TAssistant, string>? assistantFormatter)
        {
            var userText = userFormatter is null ? this.UserRequest : userFormatter(this.UserRequest);
            var assistantText = assistantFormatter is null ? this.AssistantText : assistantFormatter(this.Response);

            return
            [
                new ChatMessage("user", userText),
                new ChatMessage("assistant", assistantText)
            ];
        }
    }
}
