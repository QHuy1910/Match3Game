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

    IEnumerable<GameObject> potentialMatches;

    public SoundManager soundManager;
    
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

    // RainbowCandy kích hoạt trước animation
    if (s1.Bonus == BonusType.ClearAllOfColor)
    {
        ClearAllOfColor(s2.Type, s1);
        // ensure we reset state and restart potential matches when we early-exit
        state = GameState.None;
        StartCheckForPotentialMatches();
        yield break;
    }
    else if (s2.Bonus == BonusType.ClearAllOfColor)
    {
        ClearAllOfColor(s1.Type, s2);
        state = GameState.None;
        StartCheckForPotentialMatches();
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
    bool addBonus = totalMatches.Count() >= Constants.MinimumMatchesForBonus &&
        !BonusTypeUtilities.ContainsDestroyWholeRowColumn(hitGomatchesInfo.BonusesContained) &&
        !BonusTypeUtilities.ContainsDestroyWholeRowColumn(hitGo2matchesInfo.BonusesContained);

    bool createRainbow = totalMatches.Count() >= 5;
    Shape hitGoCache = null;

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

    if (createRainbow)
    {
        rainbowTargetGo = hitGomatchesInfo.MatchedCandy.Count() > 0 ? hitGo : hitGo2;
        willCreateRainbow = true;
        // do not create now; we'll create it after removals below
    }
    else if (addBonus)
    {
        hitGoCache = totalMatches.Count() > 0 ? hitGo.GetComponent<Shape>() : hitGo2.GetComponent<Shape>();
        if (hitGoCache != null)
        {
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

        foreach (var item in totalMatches)
        {
            // If we're going to create a Rainbow candy on one of the matched items,
            // skip destroying that specific GameObject now so we can replace it
            // with the Rainbow afterwards (avoids overlap).
            if (willCreateRainbow && rainbowTargetGo != null && item == rainbowTargetGo)
                continue;

            // If we're going to create a bonus on one of the matched items,
            // skip destroying that specific GameObject now so we can replace it
            // with the bonus afterwards (avoids duplicate/stuck visuals).
            if (willCreateBonus && bonusTargetGo != null && item == bonusTargetGo)
                continue;

            shapes.Remove(item);
            RemoveFromScene(item);
        }

        if (willCreateRainbow && rainbowTargetGo != null)
        {
            // create the Rainbow in the cleared slot
            CreateRainbowCandy(rainbowTargetGo);
            willCreateRainbow = false;
        }

        if (willCreateBonus && hitGoCache != null)
        {
            CreateBonus(hitGoCache);
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

        totalMatches = shapes.GetMatches(collapsedCandyInfo.AlteredCandy)
            .Union(shapes.GetMatches(newCandyInfo.AlteredCandy)).Distinct();

        timesRun++;
    }

    state = GameState.None;
    StartCheckForPotentialMatches();
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

                AnimatePotentialMatchesCoroutine = Utilities.AnimatePotentialMatches(potentialMatches);
                StartCoroutine(AnimatePotentialMatchesCoroutine);
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


private void ClearAllOfColor(string colorType, Shape activatingShape = null)
{
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
                RemoveFromScene(go);
                shapes[r, c] = null;
            }
        }
    }

    soundManager.PlayCrincle(); // hiệu ứng âm thanh
    IncreaseScore(1000); // thưởng điểm

    // refill lại bảng
    var columns = Enumerable.Range(0, Constants.Columns);
    var collapsed = shapes.Collapse(columns);
    var newOnes = CreateNewCandyInSpecificColumns(columns);
    MoveAndAnimate(collapsed.AlteredCandy, collapsed.MaxDistance);
    MoveAndAnimate(newOnes.AlteredCandy, newOnes.MaxDistance);
}


}
