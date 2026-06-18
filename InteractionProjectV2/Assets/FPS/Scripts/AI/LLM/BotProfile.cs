using System.Collections.Generic;
using UnityEngine;


public class BotProfile : MonoBehaviour
{
    [System.Serializable]
    public struct Relationship
    {
        public int botId;                
        [Range(-1f, 1f)] public float bond;   
        public string note;            
    }

    [Header("Identity")]
    public string characterName = "Unit";
    [TextArea(2, 5)] public string backstory;
    [TextArea(2, 4)] public string goals;
    [Tooltip("Things this bot remembers / personality seed. Shapes how it talks.")]
    [TextArea(2, 4)] public string memory;
    public List<Relationship> relationships = new List<Relationship>();

    [Header("Combat traits (affect behaviour)")]
    [Tooltip("Higher = closes the distance and fights at point blank.")]
    [Range(0f, 1f)] public float aggression = 0.5f;
    [Tooltip("Higher = retreats at a higher health threshold (more self-preserving).")]
    [Range(0f, 1f)] public float caution = 0.3f;
    [Tooltip("Higher = focus-fires onto the target of a squadmate it likes.")]
    [Range(0f, 1f)] public float teamwork = 0.5f;

    public float BondTo(int otherBotId)
    {
        foreach (var r in relationships) if (r.botId == otherBotId) return r.bond;
        return 0f;
    }

    public string RelationshipsText()
    {
        if (relationships == null || relationships.Count == 0) return "no strong feelings about squadmates";
        var parts = new List<string>();
        foreach (var r in relationships)
        {
            string feel = r.bond > 0.33f ? "trusts/likes" : r.bond < -0.33f ? "dislikes/rivals" : "neutral toward";
            string note = string.IsNullOrEmpty(r.note) ? "" : $" ({r.note})";
            parts.Add($"{feel} Bot {r.botId}{note}");
        }
        return string.Join("; ", parts);
    }
}
