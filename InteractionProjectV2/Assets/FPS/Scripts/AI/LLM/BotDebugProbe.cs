using UnityEngine;
using Unity.FPS.AI;                       
using FpsHealth = Unity.FPS.Game.Health;


public class BotDebugProbe : MonoBehaviour
{
    public string ignoreTag = "Player";
    private string _status = "starting...";

    void Update()
    {
        var all = FindObjectsByType<FpsHealth>(FindObjectsSortMode.None);

        Transform nearest = null;
        float best = Mathf.Infinity;
        int excludedSelf = 0, excludedBot = 0, excludedPlayer = 0, dead = 0;

        foreach (var h in all)
        {
            if (h == null) continue;
            if (h.CurrentHealth <= 0f) { dead++; continue; }
            if (h.transform == transform || h.transform.IsChildOf(transform)) { excludedSelf++; continue; }
            if (h.GetComponent<BotController>() != null) { excludedBot++; continue; }
            if (!string.IsNullOrEmpty(ignoreTag) && h.CompareTag(ignoreTag)) { excludedPlayer++; continue; }

            float d = Vector3.Distance(transform.position, h.transform.position);
            if (d < best) { best = d; nearest = h.transform; }
        }

        if (nearest == null)
        {
            _status = $"NO enemy found. Health objects in scene={all.Length} " +
                      $"(self={excludedSelf}, otherBots={excludedBot}, player={excludedPlayer}, dead={dead})";
            return;
        }

        Vector3 origin = transform.position + Vector3.up * 1.6f;
        Vector3 aim = nearest.position + Vector3.up * 1.1f;
        bool los;
        if (Physics.Raycast(origin, (aim - origin).normalized, out RaycastHit hit, best + 1f, ~0, QueryTriggerInteraction.Ignore))
            los = hit.collider.GetComponentInParent<FpsHealth>() != null &&
                  hit.collider.GetComponentInParent<FpsHealth>().transform == nearest;
        else
            los = true;
        Debug.DrawLine(origin, aim, los ? Color.green : Color.red);

        _status = $"nearest '{nearest.name}' at {best:F1}m  LOS={los}   <press SPACE to test-fire>";

        if (Input.GetKey(KeyCode.Space))
        {
            var ec = GetComponent<EnemyController>();
            if (ec == null) { _status += "   -> NO EnemyController on this bot!"; return; }
            ec.OrientWeaponsTowards(aim);
            bool fired = ec.TryAtack(aim);
            _status += fired ? "   -> FIRED" : "   -> TryAtack=false (weapon cooldown/swap or GameFlow)";
        }
    }

    void OnGUI()
    {
        GUI.color = Color.cyan;
        GUI.Label(new Rect(10, 130, 1000, 30), "PROBE: " + _status);
    }
}
