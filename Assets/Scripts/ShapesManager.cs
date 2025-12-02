using UnityEngine;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine.UI;


public class ShapesManager : MonoBehaviour
{
    public Text DebugText, ScoreText;
    public bool ShowDebugInfo = false;
    //candy graphics taken from http://opengameart.org/content/candy-pack-1

    public ShapesArray shapes;

    private int score;

    // Calculated at runtime so the board fits on different screen sizes / aspect ratios
    private Vector2 BottomRight;
    private Vector2 CandySize;
    private float candyScale = 1f;
    // small offset to tweak board placement from the Inspector (x: right, y: up)
    public Vector2 BoardOffset = Vector2.zero;

    private GameState state = GameState.None;
    private GameObject hitGo = null;
    private Vector2[] SpawnPositions;
    public GameObject[] CandyPrefabs;
    public GameObject[] ExplosionPrefabs;
    public GameObject[] BonusPrefabs;
    public GameObject RainbowCandyPrefab; // gán prefab trong Inspector


    private IEnumerator CheckPotentialMatchesCoroutine;
    private IEnumerator AnimatePotentialMatchesCoroutine;
    private IEnumerator ProcessBoardMatchesCoroutine;

    IEnumerable<GameObject> potentialMatches;

    // track gameobjects that were removed by a Rainbow effect so they don't spawn
    // new bonuses/rainbows when processed
    private HashSet<GameObject> clearedByRainbow = new HashSet<GameObject>();
    // When true, do not create new bonuses/rainbows during cascade processing.
    private bool suppressSpecialsBecauseOfRainbow = false;

    // lightweight container used for ClearAllOfColor preview/remove logic
    private struct ClearTarget
    {
        public int Row;
        public int Col;
        public GameObject Go;
        public ClearTarget(int row, int col, GameObject go)
        {
            Row = row; Col = col; Go = go;
        }
    }

    public SoundManager soundManager;
    // Preview settings for candies about to be destroyed
    public float PreviewDuration = 0.18f;
    public float PreviewScale = 1.15f;
    public Color PreviewTint = new Color(1f, 1f, 0.6f, 1f);

    /// <summary>
    /// Safely stop ongoing coroutines/animations and reset transient state
    /// so the board can be reinitialized (restart/premade load) without leftover state.
    /// </summary>
    private void ResetBoardForReinitialization()
    {
        // Stop potential-matches animation and its coroutine
        if (AnimatePotentialMatchesCoroutine != null)
        {
            try { StopCoroutine(AnimatePotentialMatchesCoroutine); } catch { }
            AnimatePotentialMatchesCoroutine = null;
        }

        // Stop potential-matches checker
        if (CheckPotentialMatchesCoroutine != null)
        {
            try { StopCoroutine(CheckPotentialMatchesCoroutine); } catch { }
            CheckPotentialMatchesCoroutine = null;
        }

        // Stop processing cascades if running
        if (ProcessBoardMatchesCoroutine != null)
        {
            try { StopCoroutine(ProcessBoardMatchesCoroutine); } catch { }
            ProcessBoardMatchesCoroutine = null;
        }

        // Stop any other coroutines started on this MonoBehaviour (safe reset)
        try { StopAllCoroutines(); } catch { }

        // Kill active tweens (prevents lingering animations on destroyed objects)
        try { DG.Tweening.DOTween.KillAll(); } catch { }

        // reset transient gameplay state
        state = GameState.None;
        hitGo = null;
        clearedByRainbow.Clear();
        suppressSpecialsBecauseOfRainbow = false;

        // restore opacity on any potential matches we were animating
        ResetOpacityOnPotentialMatches();
    }
    
    /// <summary>
    /// Recalculate candy world size and board origin so the board fits the camera view.
    /// </summary>
    private void UpdateBoardLayout()
    {
        // determine candy base size from the first candy prefab if possible
        Vector2 baseCandySize = Vector2.zero;
        if (CandyPrefabs != null && CandyPrefabs.Length > 0 && CandyPrefabs[0] != null)
        {
            var sr = CandyPrefabs[0].GetComponent<SpriteRenderer>();
            if (sr != null && sr.sprite != null)
            {
                // sprite.bounds.size is in local units (px-to-units applied)
                baseCandySize = new Vector2(sr.sprite.bounds.size.x * CandyPrefabs[0].transform.localScale.x,
                                           sr.sprite.bounds.size.y * CandyPrefabs[0].transform.localScale.y);
            }
        }

        // fallback to a sensible default if we couldn't read the sprite
        if (baseCandySize == Vector2.zero)
            baseCandySize = new Vector2(0.7f, 0.7f);

        var cam = Camera.main;
        if (cam == null)
            return;

        // compute grid size in world units using base size
        float gridWidth = Constants.Columns * baseCandySize.x;
        float gridHeight = Constants.Rows * baseCandySize.y;

        // visible world size (orthographic camera expected)
        float visibleHeight = cam.orthographicSize * 2f;
        float visibleWidth = visibleHeight * cam.aspect;

        // compute scale needed to fit both width and height (leave small padding)
        float scaleW = visibleWidth / (gridWidth + 0.01f);
        float scaleH = visibleHeight / (gridHeight + 0.01f);
        float scaleToFit = Mathf.Min(scaleW, scaleH, 1f) * 0.95f; // 95% to add margin
        candyScale = scaleToFit <= 0f ? 1f : scaleToFit;

        // set final candy size
        CandySize = baseCandySize * candyScale;

    // center the grid on the camera's viewport center so it stays visible on different aspect ratios
    // use ViewportToWorldPoint to get the world position of the viewport center (robust across setups)
    float distanceToPlane = Mathf.Abs(cam.transform.position.z - 0f);
    Vector3 camCenter = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, distanceToPlane));
    Vector2 bottomLeft = new Vector2(camCenter.x - (Constants.Columns * CandySize.x) / 2f,
                     camCenter.y - (Constants.Rows * CandySize.y) / 2f);

    // apply manual inspector offset (in world units)
    bottomLeft += BoardOffset;

    // nudge slightly to the right by default so the grid isn't too close to the left edge
    // this is a small fraction of a tile; adjust multiplier if you want a bigger shift
    bottomLeft += new Vector2(CandySize.x * 0.4f, 0f);

    BottomRight = bottomLeft; // historically used as bottom-left origin
    }
    void Awake()
    {
        DebugText.enabled = ShowDebugInfo;
    }

    // Use this for initialization
    void Start()
    {
        // compute layout so candies spawn inside the visible camera area
        UpdateBoardLayout();
        InitializeTypesOnPrefabShapesAndBonuses();

        InitializeCandyAndSpawnPositions();

        StartCheckForPotentialMatches();
    }

    /// <summary>
    /// Initialize shapes
    /// </summary>
    private void InitializeTypesOnPrefabShapesAndBonuses()
{
    // Gán Type cho các viên candy thường
    foreach (var item in CandyPrefabs)
    {
        var shape = item.GetComponent<Shape>();
        shape.Type = item.name;
    }

    // Gán Type cho các bonus
    foreach (var item in BonusPrefabs)
    {
        var shape = item.GetComponent<Shape>();
        string itemName = item.name;

        // Nếu là RainbowCandy thì cho Type = "RainbowCandy"
        if (itemName.Contains("Rainbow"))
        {
            shape.Type = "RainbowCandy";
            continue;
        }

        // Ngược lại (bonus thường): lấy phần sau dấu _ để xác định màu
        string[] parts = itemName.Split('_');
        if (parts.Length > 1)
        {
            string color = parts[1].Trim();
            var match = CandyPrefabs.FirstOrDefault(x => x.name.Contains(color));
            if (match != null)
                shape.Type = match.name;
        }
    }
}

    public void InitializeCandyAndSpawnPositionsFromPremadeLevel()
    {
        // ensure any in-progress swaps/animations are stopped before we reinitialize
        ResetBoardForReinitialization();
        InitializeVariables();

        var premadeLevel = DebugUtilities.FillShapesArrayFromResourcesData();

        if (shapes != null)
            DestroyAllCandy();

        shapes = new ShapesArray();
        SpawnPositions = new Vector2[Constants.Columns];

        for (int row = 0; row < Constants.Rows; row++)
        {
            for (int column = 0; column < Constants.Columns; column++)
            {

                GameObject newCandy = null;

                newCandy = GetSpecificCandyOrBonusForPremadeLevel(premadeLevel[row, column]);

                InstantiateAndPlaceNewCandy(row, column, newCandy);

            }
        }

        SetupSpawnPositions();
    }


    public void InitializeCandyAndSpawnPositions()
    {
        // ensure any in-progress swaps/animations are stopped before we reinitialize
        ResetBoardForReinitialization();
        InitializeVariables();

        if (shapes != null)
            DestroyAllCandy();

        shapes = new ShapesArray();
        SpawnPositions = new Vector2[Constants.Columns];

        for (int row = 0; row < Constants.Rows; row++)
        {
            for (int column = 0; column < Constants.Columns; column++)
            {

                GameObject newCandy = GetRandomCandy();

                //check if two previous horizontal are of the same type
                while (column >= 2 && shapes[row, column - 1].GetComponent<Shape>()
                    .IsSameType(newCandy.GetComponent<Shape>())
                    && shapes[row, column - 2].GetComponent<Shape>().IsSameType(newCandy.GetComponent<Shape>()))
                {
                    newCandy = GetRandomCandy();
                }

                //check if two previous vertical are of the same type
                while (row >= 2 && shapes[row - 1, column].GetComponent<Shape>()
                    .IsSameType(newCandy.GetComponent<Shape>())
                    && shapes[row - 2, column].GetComponent<Shape>().IsSameType(newCandy.GetComponent<Shape>()))
                {
                    newCandy = GetRandomCandy();
                }

                InstantiateAndPlaceNewCandy(row, column, newCandy);

            }
        }

        SetupSpawnPositions();
    }



    private void InstantiateAndPlaceNewCandy(int row, int column, GameObject newCandy)
    {
        GameObject go = Instantiate(newCandy,
            BottomRight + new Vector2(column * CandySize.x, row * CandySize.y), Quaternion.identity)
            as GameObject;

        // apply scale so candies fit the viewport if we computed a candyScale
        if (!Mathf.Approximately(candyScale, 1f))
            go.transform.localScale = new Vector3(go.transform.localScale.x * candyScale,
                                                 go.transform.localScale.y * candyScale,
                                                 go.transform.localScale.z);

        // assign the specific properties
        go.GetComponent<Shape>().Assign(newCandy.GetComponent<Shape>().Type, row, column);
        shapes[row, column] = go;
    }

    private void SetupSpawnPositions()
    {
        //create the spawn positions for the new shapes (will pop from the 'ceiling')
        for (int column = 0; column < Constants.Columns; column++)
        {
            SpawnPositions[column] = BottomRight
                + new Vector2(column * CandySize.x, Constants.Rows * CandySize.y);
        }
    }




    /// <summary>
    /// Destroy all candy gameobjects
    /// </summary>
    private void DestroyAllCandy()
    {
        for (int row = 0; row < Constants.Rows; row++)
        {
            for (int column = 0; column < Constants.Columns; column++)
            {
                Destroy(shapes[row, column]);
            }
        }
    }


    // Update is called once per frame
    void Update()
    {
        if (ShowDebugInfo)
            DebugText.text = DebugUtilities.GetArrayContents(shapes);

        if (state == GameState.None)
        {
            //user has clicked or touched
            if (Input.GetMouseButtonDown(0))
            {
                //get the hit position
                var hit = Physics2D.Raycast(Camera.main.ScreenToWorldPoint(Input.mousePosition), Vector2.zero);
                if (hit.collider != null) //we have a hit!!!
                {
                    hitGo = hit.collider.gameObject;
                    state = GameState.SelectionStarted;
                }
                
            }
        }
        else if (state == GameState.SelectionStarted)
        {
            //user dragged
            if (Input.GetMouseButton(0))
            {
                

                var hit = Physics2D.Raycast(Camera.main.ScreenToWorldPoint(Input.mousePosition), Vector2.zero);
                //we have a hit
                if (hit.collider != null && hitGo != hit.collider.gameObject)
                {

                    //user did a hit, no need to show him hints 
                    StopCheckForPotentialMatches();

                    //if the two shapes are diagonally aligned (different row and column), just return
                    if (!Utilities.AreVerticalOrHorizontalNeighbors(hitGo.GetComponent<Shape>(),
                        hit.collider.gameObject.GetComponent<Shape>()))
                    {
                        state = GameState.None;
                    }
                    else
                    {
                        state = GameState.Animating;
                        FixSortingLayer(hitGo, hit.collider.gameObject);
                        StartCoroutine(FindMatchesAndCollapse(hit));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Modifies sorting layers for better appearance when dragging/animating
    /// </summary>
    /// <param name="hitGo"></param>
    /// <param name="hitGo2"></param>
    private void FixSortingLayer(GameObject hitGo, GameObject hitGo2)
    {
        SpriteRenderer sp1 = hitGo.GetComponent<SpriteRenderer>();
        SpriteRenderer sp2 = hitGo2.GetComponent<SpriteRenderer>();
        if (sp1.sortingOrder <= sp2.sortingOrder)
        {
            sp1.sortingOrder = 1;
            sp2.sortingOrder = 0;
        }
    }




private IEnumerator FindMatchesAndCollapse(RaycastHit2D hit2)
{
    var hitGo2 = hit2.collider.gameObject;
    shapes.Swap(hitGo, hitGo2);

    Shape s1 = hitGo.GetComponent<Shape>();
    Shape s2 = hitGo2.GetComponent<Shape>();

    // If one of the swapped candies is a Rainbow, animate the visual swap first
    // and then trigger the Rainbow effect so the player sees the swap.
    if (s1.Bonus == BonusType.ClearAllOfColor)
    {
        // ensure proper draw order
        FixSortingLayer(hitGo, hitGo2);
        // animate swap visuals
        Vector3 pos1 = hitGo.transform.position;
        Vector3 pos2 = hitGo2.transform.position;
        hitGo.transform.DOMove(pos2, Constants.AnimationDuration);
        hitGo2.transform.DOMove(pos1, Constants.AnimationDuration);
        yield return new WaitForSeconds(Constants.AnimationDuration);

        // After animation, trigger the Rainbow effect (s2.Type is the target color)
        yield return StartCoroutine(ClearAllOfColor(s2.Type, s1));
        yield break;
    }
    else if (s2.Bonus == BonusType.ClearAllOfColor)
    {
        FixSortingLayer(hitGo, hitGo2);
        Vector3 pos1b = hitGo.transform.position;
        Vector3 pos2b = hitGo2.transform.position;
        hitGo.transform.DOMove(pos2b, Constants.AnimationDuration);
        hitGo2.transform.DOMove(pos1b, Constants.AnimationDuration);
        yield return new WaitForSeconds(Constants.AnimationDuration);

        yield return StartCoroutine(ClearAllOfColor(s1.Type, s2));
        yield break;
    }

    // Animation swap
    hitGo.transform.DOMove(hitGo2.transform.position, Constants.AnimationDuration);
    hitGo2.transform.DOMove(hitGo.transform.position, Constants.AnimationDuration);
    yield return new WaitForSeconds(Constants.AnimationDuration);

    // Lấy matches
    var hitGomatchesInfo = shapes.GetMatches(hitGo);
    var hitGo2matchesInfo = shapes.GetMatches(hitGo2);
    var totalMatches = hitGomatchesInfo.MatchedCandy
        .Union(hitGo2matchesInfo.MatchedCandy).Distinct();

    // Safety: sometimes a board-wide match can exist that the per-candy GetMatches
    // calls miss (edge cases). Also check the full board and include any matches
    // found there before deciding to undo the swap.
    var boardWide = GetAllMatches();
    if (boardWide != null)
    {
        totalMatches = totalMatches.Union(boardWide).Distinct();
    }

    // Undo swap nếu không tạo match >=3
    if (totalMatches.Count() < Constants.MinimumMatches &&
        s1.Bonus != BonusType.ClearAllOfColor &&
        s2.Bonus != BonusType.ClearAllOfColor)
    {
        hitGo.transform.DOMove(hitGo2.transform.position, Constants.AnimationDuration);
        hitGo2.transform.DOMove(hitGo.transform.position, Constants.AnimationDuration);
        yield return new WaitForSeconds(Constants.AnimationDuration);
        shapes.UndoSwap();
    }

    // Tạo bonus hoặc RainbowCandy nếu đủ match
    // Determine straight runs for rainbows (>=5) and bonuses (exactly 4)
    var straightRuns5 = GetStraightRuns(5);
    var rainbowRun = straightRuns5.FirstOrDefault(run => run.Intersect(totalMatches).Count() >= 5);
    bool createRainbow = rainbowRun != null;
    Shape hitGoCache = null;

    // For bonuses we require a straight contiguous run of exactly 4
    var straightRuns4 = GetStraightRuns(4).Where(r => r.Count == 4).ToList();
    var bonusRun = straightRuns4.FirstOrDefault(run => run.Intersect(totalMatches).Count() >= 4);
    bool addBonus = bonusRun != null &&
        !BonusTypeUtilities.ContainsDestroyWholeRowColumn(hitGomatchesInfo.BonusesContained) &&
        !BonusTypeUtilities.ContainsDestroyWholeRowColumn(hitGo2matchesInfo.BonusesContained);

    // If we need to create a Rainbow candy, don't create it immediately here.
    // Instead, remember the target GameObject and create the Rainbow after
    // we've removed the matched candies so it will occupy a cleared slot
    // (prevents overlapping and makes it swappable).
    GameObject rainbowTargetGo = null;
    bool willCreateRainbow = false;

    // For non-rainbow bonuses, also defer creation until after removals to
    // avoid duplicate/stuck visuals. We'll remember the Shape and its GameObject.
    GameObject bonusTargetGo = null;
    bool willCreateBonus = false;

    if (createRainbow && rainbowRun != null)
    {
        // prefer to convert one of the swapped candies if it belongs to the straight run
        if (rainbowRun.Contains(hitGo))
            rainbowTargetGo = hitGo;
        else if (rainbowRun.Contains(hitGo2))
            rainbowTargetGo = hitGo2;
        else
            // fallback: pick the first item in the straight run
            rainbowTargetGo = rainbowRun.FirstOrDefault();

        willCreateRainbow = rainbowTargetGo != null;
        // creation is deferred until after removals
    }
    else if (addBonus && bonusRun != null)
    {
        // choose which GameObject to convert into the bonus: prefer one of the swapped
        // prefer swapped candy if it's in the straight 4-run
        Shape chosen = null;
        if (bonusRun.Contains(hitGo))
            chosen = hitGo.GetComponent<Shape>();
        else if (bonusRun.Contains(hitGo2))
            chosen = hitGo2.GetComponent<Shape>();
        else
            chosen = bonusRun.First().GetComponent<Shape>();

        if (chosen != null)
        {
            hitGoCache = chosen;
            bonusTargetGo = hitGoCache.gameObject;
            willCreateBonus = true;
        }
        // do not create now; we'll create the bonus after removals below
    }

    int timesRun = 1;
    while (totalMatches.Count() >= Constants.MinimumMatches)
    {
        IncreaseScore((totalMatches.Count() - 2) * Constants.Match3Score);
        if (timesRun >= 2)
            IncreaseScore(Constants.SubsequentMatchScore);

        soundManager.PlayCrincle();

        // If any item in the match is a special, mark that this removal is caused by a special
        bool removalBySpecial = totalMatches.Any(i => i != null && i.GetComponent<Shape>().Bonus != BonusType.None);
        // Suppress creating new specials during this cascade if it was triggered by a special
        suppressSpecialsBecauseOfRainbow = suppressSpecialsBecauseOfRainbow || removalBySpecial;

        // Build the list of items we will actually remove this pass (skip planned targets)
        var toRemove = new List<GameObject>();
        foreach (var item in totalMatches)
        {
            if (willCreateRainbow && rainbowTargetGo != null && item == rainbowTargetGo)
                continue;
            if (willCreateBonus && bonusTargetGo != null && item == bonusTargetGo)
                continue;
            toRemove.Add(item);
        }

        // preview animation for removals (scale + tint)
        float previewDurationLocal = PreviewDuration;
        foreach (var rem in toRemove)
        {
            if (rem == null) continue;
            rem.transform.DOScale(rem.transform.localScale * PreviewScale, previewDurationLocal / 2f).SetLoops(2, LoopType.Yoyo);
            var sr = rem.GetComponent<SpriteRenderer>();
            if (sr != null)
                sr.DOColor(PreviewTint, previewDurationLocal / 2f).SetLoops(2, LoopType.Yoyo);
        }
        yield return new WaitForSeconds(previewDurationLocal);

        // perform removals
        foreach (var rem in toRemove)
        {
            if (rem == null) continue;
            if (removalBySpecial)
                clearedByRainbow.Add(rem);
            shapes.Remove(rem);
            RemoveFromScene(rem);
        }

        if (willCreateRainbow && rainbowTargetGo != null)
        {
            // only create the Rainbow if the target wasn't destroyed by other special effects
            var s = rainbowTargetGo.GetComponent<Shape>();
            if (s != null && shapes[s.Row, s.Column] == rainbowTargetGo)
            {
                // create the Rainbow in the cleared slot
                CreateRainbowCandy(rainbowTargetGo);
            }
            willCreateRainbow = false;
        }

        if (willCreateBonus && hitGoCache != null)
        {
            // ensure the chosen base shape still exists in the array (wasn't destroyed by another bonus)
            if (shapes[hitGoCache.Row, hitGoCache.Column] == hitGoCache.gameObject)
            {
                CreateBonus(hitGoCache);
            }
            willCreateBonus = false;
        }

        addBonus = false;

        var columns = totalMatches.Select(go => go.GetComponent<Shape>().Column).Distinct();
        var collapsedCandyInfo = shapes.Collapse(columns);
        var newCandyInfo = CreateNewCandyInSpecificColumns(columns);

        int maxDistance = Mathf.Max(collapsedCandyInfo.MaxDistance, newCandyInfo.MaxDistance);
        MoveAndAnimate(newCandyInfo.AlteredCandy, maxDistance);
        MoveAndAnimate(collapsedCandyInfo.AlteredCandy, maxDistance);

        yield return new WaitForSeconds(Constants.MoveAnimationMinDuration * maxDistance);

        // After collapse/spawn, check the entire board for newly formed matches
        totalMatches = GetAllMatches();

        timesRun++;
    }

    state = GameState.None;
    StartCheckForPotentialMatches();
}

    /// <summary>
    /// Scans the entire board and returns all matched GameObjects (>= MinimumMatches),
    /// excluding any objects that were removed by a Rainbow effect.
    /// </summary>
    /// <returns></returns>
    private IEnumerable<GameObject> GetAllMatches()
    {
        List<GameObject> matches = new List<GameObject>();

        // Horizontal scan: for each row, find contiguous runs of the same Type
        for (int r = 0; r < Constants.Rows; r++)
        {
            int runStart = 0;
            string runType = null;
            for (int c = 0; c <= Constants.Columns; c++)
            {
                GameObject go = (c < Constants.Columns) ? shapes[r, c] : null;
                string t = go != null ? go.GetComponent<Shape>().Type : null;

                if (t != null && runType == null)
                {
                    // start new run
                    runType = t;
                    runStart = c;
                }
                else if (t != null && runType == t)
                {
                    // continue run
                }
                else
                {
                    // run ended at c-1
                    int runLength = c - runStart;
                    if (runType != null && runLength >= Constants.MinimumMatches)
                    {
                        for (int x = runStart; x < c; x++)
                        {
                            var m = shapes[r, x];
                            if (m != null && !clearedByRainbow.Contains(m))
                                matches.Add(m);
                        }
                    }
                    // reset run if next cell starts a new one
                    runType = t;
                    runStart = c;
                }
            }
        }

        // Vertical scan: for each column, find contiguous runs of the same Type
        for (int c = 0; c < Constants.Columns; c++)
        {
            int runStart = 0;
            string runType = null;
            for (int r = 0; r <= Constants.Rows; r++)
            {
                GameObject go = (r < Constants.Rows) ? shapes[r, c] : null;
                string t = go != null ? go.GetComponent<Shape>().Type : null;

                if (t != null && runType == null)
                {
                    runType = t;
                    runStart = r;
                }
                else if (t != null && runType == t)
                {
                    // continue
                }
                else
                {
                    int runLength = r - runStart;
                    if (runType != null && runLength >= Constants.MinimumMatches)
                    {
                        for (int y = runStart; y < r; y++)
                        {
                            var m = shapes[y, c];
                            if (m != null && !clearedByRainbow.Contains(m))
                                matches.Add(m);
                        }
                    }
                    runType = t;
                    runStart = r;
                }
            }
        }

        return matches.Distinct();
    }

    /// <summary>
    /// Finds all straight horizontal or vertical runs of a given minimum length.
    /// Returns a list of runs where each run is a list of contiguous GameObjects.
    /// Runs exclude objects that were cleared by a Rainbow.
    /// </summary>
    private List<List<GameObject>> GetStraightRuns(int requiredLength)
    {
        var runs = new List<List<GameObject>>();

        // Horizontal
        for (int r = 0; r < Constants.Rows; r++)
        {
            int runStart = 0;
            string runType = null;
            for (int c = 0; c <= Constants.Columns; c++)
            {
                GameObject go = (c < Constants.Columns) ? shapes[r, c] : null;
                string t = go != null ? go.GetComponent<Shape>().Type : null;

                if (t != null && runType == null)
                {
                    runType = t;
                    runStart = c;
                }
                else if (t != null && runType == t)
                {
                    // continue
                }
                else
                {
                    int runLength = c - runStart;
                    if (runType != null && runLength >= requiredLength)
                    {
                        var run = new List<GameObject>();
                        for (int x = runStart; x < c; x++)
                        {
                            var m = shapes[r, x];
                            if (m != null && !clearedByRainbow.Contains(m))
                                run.Add(m);
                        }
                        if (run.Count >= requiredLength)
                            runs.Add(run);
                    }
                    runType = t;
                    runStart = c;
                }
            }
        }

        // Vertical
        for (int c = 0; c < Constants.Columns; c++)
        {
            int runStart = 0;
            string runType = null;
            for (int r = 0; r <= Constants.Rows; r++)
            {
                GameObject go = (r < Constants.Rows) ? shapes[r, c] : null;
                string t = go != null ? go.GetComponent<Shape>().Type : null;

                if (t != null && runType == null)
                {
                    runType = t;
                    runStart = r;
                }
                else if (t != null && runType == t)
                {
                    // continue
                }
                else
                {
                    int runLength = r - runStart;
                    if (runType != null && runLength >= requiredLength)
                    {
                        var run = new List<GameObject>();
                        for (int y = runStart; y < r; y++)
                        {
                            var m = shapes[y, c];
                            if (m != null && !clearedByRainbow.Contains(m))
                                run.Add(m);
                        }
                        if (run.Count >= requiredLength)
                            runs.Add(run);
                    }
                    runType = t;
                    runStart = r;
                }
            }
        }

        return runs;
    }


    /// <summary>
    /// Spawns new candy in columns that have missing ones
    /// </summary>
    /// <param name="columnsWithMissingCandy"></param>
    /// <returns>Info about new candies created</returns>
    private AlteredCandyInfo CreateNewCandyInSpecificColumns(IEnumerable<int> columnsWithMissingCandy)
{
    AlteredCandyInfo newCandyInfo = new AlteredCandyInfo();

    foreach (int column in columnsWithMissingCandy)
    {
        var emptyItems = shapes.GetEmptyItemsOnColumn(column);
        foreach (var item in emptyItems)
        {
            // Kiểm tra nếu đã có RainbowCandy, spawn candy mới trên đỉnh
            var existing = shapes[item.Row, item.Column];
            Vector3 spawnPos = SpawnPositions[column];
            if (existing != null && existing.GetComponent<Shape>().Bonus == BonusType.ClearAllOfColor)
            {
                spawnPos += Vector3.up * CandySize.y; // đẩy lên trên
            }

            var go = GetRandomCandy();
            GameObject newCandy = Instantiate(go, spawnPos, Quaternion.identity) as GameObject;
            // apply scale from layout calculation
            if (!Mathf.Approximately(candyScale, 1f))
                newCandy.transform.localScale = new Vector3(newCandy.transform.localScale.x * candyScale,
                                                            newCandy.transform.localScale.y * candyScale,
                                                            newCandy.transform.localScale.z);

            newCandy.GetComponent<Shape>().Assign(go.GetComponent<Shape>().Type, item.Row, item.Column);

            if (Constants.Rows - item.Row > newCandyInfo.MaxDistance)
                newCandyInfo.MaxDistance = Constants.Rows - item.Row;

            shapes[item.Row, item.Column] = newCandy;
            newCandyInfo.AddCandy(newCandy);
        }
    }

    return newCandyInfo;
}



    /// <summary>
    /// Animates gameobjects to their new position
    /// </summary>
    /// <param name="movedGameObjects"></param>
    private void MoveAndAnimate(IEnumerable<GameObject> movedGameObjects, int distance)
    {
        foreach (var item in movedGameObjects)
        {
            item.transform.DOMove(
                BottomRight + new Vector2(item.GetComponent<Shape>().Column * CandySize.x,
                    item.GetComponent<Shape>().Row * CandySize.y),
                Constants.MoveAnimationMinDuration * distance
            );
        }
    }

    /// <summary>
    /// Destroys the item from the scene and instantiates a new explosion gameobject
    /// </summary>
    /// <param name="item"></param>
    private void RemoveFromScene(GameObject item)
    {
        GameObject explosion = GetRandomExplosion();
        var newExplosion = Instantiate(explosion, item.transform.position, Quaternion.identity) as GameObject;
        Destroy(newExplosion, Constants.ExplosionDuration);
        Destroy(item);
    }

    /// <summary>
    /// Get a random candy
    /// </summary>
    /// <returns></returns>
    private GameObject GetRandomCandy()
    {
        return CandyPrefabs[Random.Range(0, CandyPrefabs.Length)];
    }

    private void InitializeVariables()
    {
        score = 0;
        ShowScore();
    }

    private void IncreaseScore(int amount)
    {
        score += amount;
        ShowScore();
    }

    private void ShowScore()
    {
        ScoreText.text = "Score: " + score.ToString();
    }

    /// <summary>
    /// Get a random explosion
    /// </summary>
    /// <returns></returns>
    private GameObject GetRandomExplosion()
    {
        return ExplosionPrefabs[Random.Range(0, ExplosionPrefabs.Length)];
    }

    /// <summary>
    /// Gets the specified Bonus for the specific type
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    private GameObject GetBonusFromType(string type)
    {
        string color = type.Split('_')[1].Trim();
        foreach (var item in BonusPrefabs)
        {
            if (item.GetComponent<Shape>().Type.Contains(color))
                return item;
        }
        throw new System.Exception("Wrong type");
    }

    /// <summary>
    /// Starts the coroutines, keeping a reference to stop later
    /// </summary>
    private void StartCheckForPotentialMatches()
    {
        StopCheckForPotentialMatches();
        // clear any tracked rainbow-cleared objects before starting hint coroutines
        clearedByRainbow.Clear();
        //get a reference to stop it later
        CheckPotentialMatchesCoroutine = CheckPotentialMatches();
        StartCoroutine(CheckPotentialMatchesCoroutine);
    }

    /// <summary>
    /// Stops the coroutines
    /// </summary>
    private void StopCheckForPotentialMatches()
    {
        if (AnimatePotentialMatchesCoroutine != null)
            StopCoroutine(AnimatePotentialMatchesCoroutine);
        if (CheckPotentialMatchesCoroutine != null)
            StopCoroutine(CheckPotentialMatchesCoroutine);
        ResetOpacityOnPotentialMatches();
    }

    /// <summary>
    /// Resets the opacity on potential matches (probably user dragged something?)
    /// </summary>
    private void ResetOpacityOnPotentialMatches()
    {
        if (potentialMatches != null)
            foreach (var item in potentialMatches)
            {
                if (item == null) break;

                Color c = item.GetComponent<SpriteRenderer>().color;
                c.a = 1.0f;
                item.GetComponent<SpriteRenderer>().color = c;
            }
    }

    /// <summary>
    /// Finds potential matches
    /// </summary>
    /// <returns></returns>
    private IEnumerator CheckPotentialMatches()
    {
        yield return new WaitForSeconds(Constants.WaitBeforePotentialMatchesCheck);
        potentialMatches = Utilities.GetPotentialMatches(shapes);
        if (potentialMatches != null)
        {
            while (true)
            {

                AnimatePotentialMatchesCoroutine = Utilities.AnimatePotentialMatches(potentialMatches, Constants.HintVisibleDuration);
                StartCoroutine(AnimatePotentialMatchesCoroutine);
                // wait the configured hint-visible duration before continuing the loop
                yield return new WaitForSeconds(Constants.WaitBeforePotentialMatchesCheck);
            }
        }
    }

    /// <summary>
    /// Gets a specific candy or Bonus based on the premade level information.
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    private GameObject GetSpecificCandyOrBonusForPremadeLevel(string info)
    {
        var tokens = info.Split('_');

        if (tokens.Count() == 1)
        {
            foreach (var item in CandyPrefabs)
            {
                if (item.GetComponent<Shape>().Type.Contains(tokens[0].Trim()))
                    return item;
            }

        }
        else if (tokens.Count() == 2 && tokens[1].Trim() == "B")
        {
            foreach (var item in BonusPrefabs)
            {
                if (item.name.Contains(tokens[0].Trim()))
                    return item;
            }
        }

        throw new System.Exception("Wrong type, check your premade level");
    }

// [SerializeField] private GameObject RainbowCandyPrefab; // assign trong Inspector

private void CreateBonus(Shape baseShape)
{
    if (baseShape == null) return;

    // Lấy prefab bonus dựa theo type của baseShape
    GameObject prefab = BonusPrefabs.FirstOrDefault(b => b.GetComponent<Shape>().Type == baseShape.Type);
    if (prefab == null)
    {
        Debug.LogError("No bonus prefab found for type " + baseShape.Type);
        return;
    }

    Vector3 spawnPos = baseShape.transform.position;
    int row = baseShape.Row;
    int col = baseShape.Column;

    Destroy(baseShape.gameObject);

    GameObject bonus = Instantiate(prefab, spawnPos, Quaternion.identity);
    if (!Mathf.Approximately(candyScale, 1f))
        bonus.transform.localScale = new Vector3(bonus.transform.localScale.x * candyScale,
                                                bonus.transform.localScale.y * candyScale,
                                                bonus.transform.localScale.z);
    Shape bonusShape = bonus.GetComponent<Shape>();
    bonusShape.Assign(baseShape.Type, row, col);
    bonusShape.Bonus |= BonusType.DestroyWholeRowColumn;

    shapes[row, col] = bonus;
}

private void CreateRainbowCandy(GameObject baseCandy)
{
    if (baseCandy == null || RainbowCandyPrefab == null)
    {
        Debug.LogError("RainbowCandyPrefab not assigned!");
        return;
    }

    Shape s = baseCandy.GetComponent<Shape>();
    Vector3 spawnPos = baseCandy.transform.position;
    int row = s.Row;
    int col = s.Column;

    Destroy(baseCandy);

    GameObject rainbow = Instantiate(RainbowCandyPrefab, spawnPos, Quaternion.identity);
    if (!Mathf.Approximately(candyScale, 1f))
        rainbow.transform.localScale = new Vector3(rainbow.transform.localScale.x * candyScale,
                                                  rainbow.transform.localScale.y * candyScale,
                                                  rainbow.transform.localScale.z);
    Shape rainbowShape = rainbow.GetComponent<Shape>();
    rainbowShape.Assign("Rainbow", row, col);
    rainbowShape.Bonus = BonusType.ClearAllOfColor;

    // SortingOrder cao để không bị chồng candy thường
    var sr = rainbow.GetComponent<SpriteRenderer>();
    if (sr != null) sr.sortingOrder = 2;

    shapes[row, col] = rainbow;
}


    private IEnumerator ProcessBoardMatches()
    {
        // small delay to allow previous animations to settle
        yield return new WaitForSeconds(Constants.MoveAnimationMinDuration);

        while (true)
        {
            var totalMatches = GetAllMatches().ToList();
            if (totalMatches.Count < Constants.MinimumMatches)
                break;

            IncreaseScore((totalMatches.Count - 2) * Constants.Match3Score);
            soundManager.PlayCrincle();

            // Determine groups by type so we can create specials when needed
            var typeGroups = totalMatches
                .Where(g => g != null && !clearedByRainbow.Contains(g))
                .GroupBy(go => go.GetComponent<Shape>().Type)
                .ToList();

            // If a Rainbow triggered the cascade recently, suppress creating new special candies
            var rainbowTargets = new List<GameObject>();
            var bonusTargets = new List<GameObject>();
            if (!suppressSpecialsBecauseOfRainbow)
            {
                // For cascades create Rainbows only for straight 5+ runs
                var straightRuns5 = GetStraightRuns(5);
                foreach (var run in straightRuns5)
                {
                    if (run.Intersect(totalMatches).Count() >= 5)
                    {
                        var target = run.FirstOrDefault();
                        if (target != null)
                            rainbowTargets.Add(target);
                    }
                }

                // For cascades create Bonuses only for straight runs of exactly 4
                var straightRuns4 = GetStraightRuns(4).Where(r => r.Count == 4).ToList();
                foreach (var run in straightRuns4)
                {
                    if (run.Intersect(totalMatches).Count() >= 4)
                    {
                        var target = run.FirstOrDefault();
                        if (target != null)
                            bonusTargets.Add(target);
                    }
                }
            }

            // Remove matched items, skipping planned targets so they can be replaced
            // If any item in the match is a special, treat this as a special-triggered removal
            bool removalBySpecial = totalMatches.Any(i => i != null && i.GetComponent<Shape>().Bonus != BonusType.None);
            if (removalBySpecial)
                suppressSpecialsBecauseOfRainbow = true;

            var toRemoveCascade = new List<GameObject>();
            foreach (var item in totalMatches)
            {
                if (rainbowTargets.Contains(item) || bonusTargets.Contains(item))
                    continue;
                toRemoveCascade.Add(item);
            }

            // preview animation (scale + tint)
            float previewDurationCascade = PreviewDuration;
            foreach (var rem in toRemoveCascade)
            {
                if (rem == null) continue;
                rem.transform.DOScale(rem.transform.localScale * PreviewScale, previewDurationCascade / 2f).SetLoops(2, LoopType.Yoyo);
                var sr = rem.GetComponent<SpriteRenderer>();
                if (sr != null)
                    sr.DOColor(PreviewTint, previewDurationCascade / 2f).SetLoops(2, LoopType.Yoyo);
            }
            yield return new WaitForSeconds(previewDurationCascade);

            foreach (var rem in toRemoveCascade)
            {
                if (rem == null) continue;
                if (removalBySpecial)
                    clearedByRainbow.Add(rem);
                shapes.Remove(rem);
                RemoveFromScene(rem);
            }

            // Create rainbows
            foreach (var rt in rainbowTargets)
            {
                var s = rt.GetComponent<Shape>();
                if (s != null && shapes[s.Row, s.Column] == rt)
                {
                    CreateRainbowCandy(rt);
                }
            }

            // Create bonuses
            foreach (var bt in bonusTargets)
            {
                var bs = bt.GetComponent<Shape>();
                if (bs != null && shapes[bs.Row, bs.Column] == bt)
                {
                    CreateBonus(bs);
                }
            }

            var columns = totalMatches.Select(go => go.GetComponent<Shape>().Column).Distinct();
            var collapsedInfo = shapes.Collapse(columns);
            var newCandyInfo = CreateNewCandyInSpecificColumns(columns);

            int maxDistance = Mathf.Max(collapsedInfo.MaxDistance, newCandyInfo.MaxDistance);
            MoveAndAnimate(newCandyInfo.AlteredCandy, maxDistance);
            MoveAndAnimate(collapsedInfo.AlteredCandy, maxDistance);

            yield return new WaitForSeconds(Constants.MoveAnimationMinDuration * maxDistance);
        }

        // ensure we reset state and restart potential match hints
        state = GameState.None;
        // clear the suppress flag after cascades are processed
        suppressSpecialsBecauseOfRainbow = false;
        StartCheckForPotentialMatches();
    }

    private IEnumerator ClearAllOfColor(string colorType, Shape activatingShape = null)
    {
    // indicate cascades started by a Rainbow should not produce new specials
    suppressSpecialsBecauseOfRainbow = true;
    // collect targets first so we can preview them simultaneously
    var toClear = new List<ClearTarget>();
    for (int r = 0; r < Constants.Rows; r++)
    {
        for (int c = 0; c < Constants.Columns; c++)
        {
            var go = shapes[r, c];
            if (go == null) continue;

            Shape s = go.GetComponent<Shape>();
            
            // Xóa tất cả viên cùng màu, bỏ qua viên đang activate nếu muốn
                if (s.Type == colorType || go == activatingShape?.gameObject)
                {
                    toClear.Add(new ClearTarget(r, c, go));
                }
        }
    }

        if (toClear.Count == 0)
            yield break;

        // preview animation: small pulse so the player sees which candies will be removed
        float previewDuration = 0.18f;
        foreach (var t in toClear)
        {
            var go = t.Go;
            if (go == null) continue;
            clearedByRainbow.Add(go);
            // pulse scale
            go.transform.DOScale(go.transform.localScale * 1.15f, previewDuration / 2f).SetLoops(2, LoopType.Yoyo);
        }

        yield return new WaitForSeconds(previewDuration);

        // remove after preview
        foreach (var t in toClear)
        {
            int r = t.Row;
            int c = t.Col;
            var go = t.Go;
            if (go == null) continue;
            shapes[r, c] = null;
            RemoveFromScene(go);
        }
    IncreaseScore(1000); // thưởng điểm

    // refill lại bảng
    // refill the board
    var columns = Enumerable.Range(0, Constants.Columns);
    var collapsed = shapes.Collapse(columns);
    var newOnes = CreateNewCandyInSpecificColumns(columns);
    MoveAndAnimate(collapsed.AlteredCandy, collapsed.MaxDistance);
    MoveAndAnimate(newOnes.AlteredCandy, newOnes.MaxDistance);

    int maxDistance = Mathf.Max(collapsed.MaxDistance, newOnes.MaxDistance);
    yield return new WaitForSeconds(Constants.MoveAnimationMinDuration * maxDistance);

    // start processing matches created by the cascade
    ProcessBoardMatchesCoroutine = ProcessBoardMatches();
    StartCoroutine(ProcessBoardMatchesCoroutine);
}


}
