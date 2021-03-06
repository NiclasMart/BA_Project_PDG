using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Generator : MonoBehaviour
{
  [SerializeField] int tileSize;  //how big the prefab tiles are

  // parameter for dungeon size
  [Header("Dungeon Parameters")]
  [SerializeField, Min(1)] int dungeonSize = 100;
  [SerializeField, Min(1)] int roomCount = 50;
  [SerializeField] int pathWidth = 2;
  [SerializeField, Range(0, 1)] float compressFactor = 0.1f;
  [SerializeField] bool allowPathCrossings = true;
  [SerializeField, Range(0, 1)] float connectionDegree = 0.3f;
  [SerializeField, Range(0, 1)] float shape = 0;
  [SerializeField] Texture2D dungeonShapeBlueprint;

  [Header("Room Parameters")]

  [SerializeField] Vector2Int roomDimensionX = new Vector2Int(5, 10);
  [SerializeField] Vector2Int roomDimensionY = new Vector2Int(5, 10);
  [SerializeField] Vector2Int roomDistance = new Vector2Int(0, 10);
  [SerializeField] bool useNormalRooms = true;
  [SerializeField, Min(1)] float normalRoomFrequency = 1f;
  [SerializeField] bool useBlueprintRooms = false;
  [SerializeField] List<BluePrintRoomData> roomBlueprints;

  enum RoomPosition { Center, Edge, Random };
  [Header("Path Parameters")]
  [SerializeField] RoomPosition startRoomPosition;
  [SerializeField] RoomPosition endRoomPosition;
  [SerializeField, Min(0)] int minPathLength;
  [SerializeField, Min(1)] int maxPathLength;
  [SerializeField] bool useFullDungeonSize;

  [Header("Tile Parameters")]
  [SerializeField] TileSetTable tileSetTable;
  [SerializeField] bool generateCeiling = false;
  [SerializeField, Min(0)] int dungeonHeight = 1;
  [SerializeField] GameObject debugCube;


  BitMatrix roomMatrix, pathMatrix;
  Graph roomsGraph;
  List<Path> paths = new List<Path>();
  List<Room> eventRooms = new List<Room>();
  Room startRoom, endRoom;
  int currentRoomCount = 1, iterationCount = 0;
  Vector2Int shapeArea;
  float normalRoomProbability = 1;
  List<int> roomLookup = new List<int>();

  public void StartGeneration()
  {
    Initialize();

    StartStructureGeneration();
    StartIterativeImproving();
    GenerateAdditionalConnections();
    GeneratePath();
    PlaceTiles();
  }

  private void Initialize()
  {
    roomMatrix = new BitMatrix(dungeonSize);
    pathMatrix = new BitMatrix(dungeonSize);
    roomsGraph = new Graph();

    if (dungeonShapeBlueprint != null)
    {
      shape = 0;

    }
    PreCalculateShapeArea();
    BuildRoomLookup();
  }

  private void PreCalculateShapeArea()
  {
    float shapeAreaSize = Mathf.Max(dungeonSize - (shape * dungeonSize), 2 * roomDimensionX.x);
    float leftBorder = dungeonSize / 2 - shapeAreaSize / 2;
    float rightBorder = dungeonSize / 2 + shapeAreaSize / 2;
    shapeArea = new Vector2Int((int)leftBorder, (int)rightBorder);
  }

  private void BuildRoomLookup()
  {
    if (!useBlueprintRooms)
    {
      normalRoomProbability = 1;
      useNormalRooms = true;
      return;
    }

    int blueprintRoomFrequency = 0;
    for (int i = 0; i < roomBlueprints.Count; i++)
    {
      blueprintRoomFrequency += roomBlueprints[i].frequency;
      for (int amount = 0; amount < roomBlueprints[i].frequency; amount++)
      {
        roomLookup.Add(i);
      }
    }
    normalRoomProbability = normalRoomFrequency / blueprintRoomFrequency;
  }

  private void StartStructureGeneration()
  {
    Vector2Int startPosition = Vector2Int.zero;
    List<Vector2Int> startPointList = null;

    if (dungeonShapeBlueprint != null) startPointList = SearchStartPosition();
    else startPosition = new Vector2Int(roomMatrix.size / 2, roomMatrix.size / 2);

    Room newRoom;
    do
    {
      if (dungeonShapeBlueprint != null) startPosition = startPointList[Random.Range(0, startPointList.Count)];
      newRoom = GenerateRoom();
      newRoom.SetPosition(startPosition);
    } while (!RoomPositionIsValid(newRoom));

    SetDebugBlock(new Vector2Int(startPosition.x, startPosition.y));

    SaveRoomToBitMatrix(newRoom);
    roomsGraph.AddNode(newRoom);

    GenerateRecursivly(newRoom);
  }

  private List<Vector2Int> SearchStartPosition()
  {
    //search for markt starting points within the blueprint and check if blueprint is valid
    List<Vector2Int> startPoints = new List<Vector2Int>();
    for (int i = 0; i < dungeonShapeBlueprint.width; i++)
    {
      for (int j = 0; j < dungeonShapeBlueprint.height; j++)
      {
        Color test = dungeonShapeBlueprint.GetPixel(i, j);
        if (dungeonShapeBlueprint.GetPixel(i, j) == Color.red)
        {
          startPoints.Add(new Vector2Int(i * dungeonSize / dungeonShapeBlueprint.width, (dungeonShapeBlueprint.height - j - 1) * dungeonSize / dungeonShapeBlueprint.height));
        }
      }
    }

    if (startPoints.Count != 0) return startPoints;

    //search for 3 random points if no starting point is markt within the blueprint
    do
    {
      int rngX = Random.Range(0, dungeonShapeBlueprint.width);
      int rngY = Random.Range(0, dungeonShapeBlueprint.height);
      if (dungeonShapeBlueprint.GetPixel(rngX, rngY) != Color.black)
        startPoints.Add(new Vector2Int(rngX * dungeonSize / dungeonShapeBlueprint.width, (dungeonShapeBlueprint.height - rngY - 1) * dungeonSize / dungeonShapeBlueprint.height));
    } while (startPoints.Count < 3);

    return startPoints;

  }

  List<Room> newRoomsQueue = new List<Room>();
  void GenerateRecursivly(Room parentRoom)
  {
    for (int direction = 0; direction < 4; direction++)
    {
      if (currentRoomCount >= roomCount) continue;

      //generate room with path in several attempts
      for (int attempt = 0; attempt < 1 + (compressFactor + 0.005f) * 10; attempt++)
      {
        if (Generate(parentRoom, direction)) break;
      }
    }

    //call generation recursively for each generated room
    while (newRoomsQueue.Count > 0)
    {
      int index = Random.Range(0, newRoomsQueue.Count);
      Room nextRoom = newRoomsQueue[index];
      newRoomsQueue.RemoveAt(index);
      GenerateRecursivly(nextRoom);
    }
  }

  private bool Generate(Room parentRoom, int i)
  {
    //north 11 - 3, west 00 - 0, south 10 - 2, west 01 - 1
    int axis = (i >> 1) & 1; //if x (axis == 0) or y (axis == 1)
    int direction = (i & 1) == 1 ? -1 : 1; //if positive or negative
    int useParentRoomOffset = (i ^ 1) & 1; //use room size as offset parameter

    Room newRoom = GenerateRoom();

    //calculate distance offset parameters
    Vector2Int minOffset = (parentRoom.GetSize() * useParentRoomOffset) + (newRoom.GetSize() * (useParentRoomOffset ^ 1));
    int randomRoomDistanceOffset = Random.Range(roomDistance.x, roomDistance.y);
    Vector2Int axisOffsetVector = new Vector2Int((randomRoomDistanceOffset + minOffset.x) * (axis ^ 1), (randomRoomDistanceOffset + minOffset.y) * axis);

    //calculate room offset Position
    Vector2Int newPos = parentRoom.position + direction * axisOffsetVector;

    //calculate room side shift
    int xSideOffset = Random.Range(-newRoom.GetSize().x + pathWidth, parentRoom.GetSize().x - pathWidth) * axis;
    int ySideOffset = Random.Range(-newRoom.GetSize().y + pathWidth, parentRoom.GetSize().y - pathWidth) * (axis ^ 1);
    Vector2Int randomSideOffset = new Vector2Int(xSideOffset, ySideOffset);

    //calculate final room position
    newPos += randomSideOffset;
    newRoom.SetPosition(newPos);

    if (!RoomPositionIsValid(newRoom)) return false;

    //calculate path
    if (randomRoomDistanceOffset != 0 || newRoom is BluePrintRoom || parentRoom is BluePrintRoom)
    {
      //calculate path start position
      int adjustPathOffset = (randomSideOffset.x + randomSideOffset.y) < 0 ? 0 : 1;
      Vector2Int minPathOffset = (useParentRoomOffset == 1) ? minOffset * new Vector2Int(axis ^ 1, axis) : new Vector2Int(axis ^ 1, axis) * -1;
      Vector2Int pathOrigin = parentRoom.position + randomSideOffset * adjustPathOffset + minPathOffset;

      //calculate path random offset
      int pathXOffset = 0, pathYOffset = 0;
      if (axis == 1)
      {
        int pathXOffsetRange = Mathf.Min(parentRoom.GetBottomRight().x, newRoom.GetBottomRight().x) - pathOrigin.x;
        pathXOffset = Random.Range(0, pathXOffsetRange - (pathWidth - 1));
      }
      else
      {
        int pathYOffsetRange = Mathf.Min(parentRoom.GetBottomRight().y, newRoom.GetBottomRight().y) - pathOrigin.y;
        pathYOffset = Random.Range(0, pathYOffsetRange - (pathWidth - 1));
      }
      Vector2Int randomPathOffset = new Vector2Int(pathXOffset, pathYOffset);
      pathOrigin += randomPathOffset;

      //create new path and check if its valid
      Path newPath = CreatePath(pathOrigin, randomRoomDistanceOffset, direction, axis);
      if (!PathPositionIsValid(newPath, axis)) return false;

      //finalizing path generation
      if (newRoom is BluePrintRoom) FillPathInBlueprintRoom(newRoom, newPath, axis, direction);
      if (parentRoom is BluePrintRoom) FillPathInBlueprintRoom(parentRoom, newPath, axis, -direction);
      newPath.AddConnections(parentRoom, newRoom);
      SavePathToBitMatrix(newPath);
      paths.Add(newPath);
    }
    SaveNewRoom(parentRoom, newRoom);
    return true;
  }

  private void FillPathInBlueprintRoom(Room room, Path path, int axis, int direction)
  {
    Vector2Int pathDirection = new Vector2Int((axis ^ 1), axis) * direction;
    BluePrintRoom bRoom = room as BluePrintRoom;
    Vector2Int pathStartPoint = pathDirection.x + pathDirection.y < 0 ? path.position : path.position + (path.GetSize() - Vector2Int.one) * new Vector2Int(axis ^ 1, axis);

    bool setPath = false;
    int iterator = 1;
    Vector2Int pos = Vector2Int.zero, blueprintIndex;
    do
    {
      setPath = false;
      for (int i = 0; i < pathWidth; i++)
      {
        pos = pathStartPoint + pathDirection * iterator + new Vector2Int(axis, axis ^ 1) * i;
        blueprintIndex = pos - bRoom.position;
        if (!bRoom.GetBlueprintPixel(blueprintIndex.x, blueprintIndex.y)) setPath = true;
      }
      iterator++;
    } while (setPath);

    if (pathDirection.x + pathDirection.y < 0)
    {
      path.size += new Vector2Int((axis ^ 1), axis) * (iterator - 1);
      path.SetPosition(path.position + pathDirection * (iterator - 1));
    }
    else path.size += (pathDirection * (iterator - 1));
  }

  Room GenerateRoom()
  {
    Room newRoom;
    if (useNormalRooms && Random.Range(0, 1f) <= normalRoomProbability)
    {
      int sizeX = Random.Range(roomDimensionX.x, roomDimensionX.y);
      int sizeY = Random.Range(roomDimensionY.x, roomDimensionY.y);
      newRoom = new Room(new Vector2Int(sizeX, sizeY));
    }
    else
    {
      int rng = Random.Range(0, roomLookup.Count);
      newRoom = new BluePrintRoom(Vector2Int.one, roomBlueprints[roomLookup[rng]].roomBluePrint, roomBlueprints[roomLookup[rng]].randomRotaion);
    }
    return newRoom;
  }

  private Path CreatePath(Vector2Int pathOrigin, int length, int direction, int axis)
  {
    int xSize, ySize;
    (xSize, ySize) = axis == 0 ? (length, pathWidth) : (pathWidth, length);
    Path newPath = new Path(new Vector2Int(xSize, ySize));
    newPath.connectionPoint = pathOrigin;
    //sets the start point of the path so that it always is in the top left corner
    newPath.position = direction == -1 ? pathOrigin - new Vector2Int((axis ^ 1), axis) * (newPath.GetSize() - Vector2Int.one) : pathOrigin;
    return newPath;
  }

  //finalizes the room generation step
  //saves room and sets up connections
  private void SaveNewRoom(Room parentRoom, Room newRoom)
  {
    SaveRoomToBitMatrix(newRoom);
    currentRoomCount++;
    parentRoom.AddConnection(newRoom);
    newRoom.AddConnection(parentRoom);
    newRoomsQueue.Add(newRoom);
    roomsGraph.AddNode(newRoom);
  }

  void SaveRoomToBitMatrix(Room room)
  {
    for (int i = room.position.x; i < room.position.x + room.GetSize().x; i++)
    {
      for (int j = room.position.y; j < room.position.y + room.GetSize().y; j++)
      {
        if (room is BluePrintRoom)
        {
          if ((room as BluePrintRoom).GetBlueprintPixel(i - room.position.x, j - room.position.y)) roomMatrix.SetValue(i, j, true);
        }
        else roomMatrix.SetValue(i, j, true);
      }
    }
  }

  void SavePathToBitMatrix(Path path)
  {
    //iterate over all path tiles
    for (int i = 0; i < path.GetSize().x; i++)
    {
      for (int j = 0; j < path.GetSize().y; j++)
      {
        Vector2Int pathTileIndex = path.position + new Vector2Int(i, j);

        //handle path crossing within node system
        if (pathMatrix.GetValue(pathTileIndex.x, pathTileIndex.y))
        {
          Path crossPath = GetPathAtPosition(pathTileIndex);
          if (crossPath == null) break;

          AddCrossingConnections(path, crossPath);
        }
        pathMatrix.SetValue(pathTileIndex.x, pathTileIndex.y, true);
      }
    }
  }

  //sets all requiered connections for the node system 
  private void AddCrossingConnections(Path path, Path crossPath)
  {
    foreach (var room in crossPath.connections)
    {
      foreach (var newRoom in path.connections)
      {
        if (Vector3.Distance(room.GetCenterWorld(), newRoom.GetCenterWorld()) > 2 * roomDistance.y) continue;

        room.AddConnection(newRoom);
        newRoom.AddConnection(room);
      }
    }
    crossPath.AddConnections(path.connections);
    path.AddConnections(crossPath.connections);
  }

  //searches for existing path on given position
  private Path GetPathAtPosition(Vector2Int gridPos)
  {
    return paths.Find(i => (i.position.x <= gridPos.x
                        && (i.position + i.GetSize()).x >= gridPos.x
                        && i.position.y <= gridPos.y
                        && (i.position + i.GetSize()).y >= gridPos.y));
  }

  bool RoomPositionIsValid(Room room)
  {
    //check if room position is within the allowed grid zone
    if (room.position.x < shapeArea.x + 1 || room.position.x + room.GetSize().x > shapeArea.y - 1) return false;
    if (room.position.y < 1 || room.position.y + room.GetSize().y > roomMatrix.size - 1) return false;

    if (!EnoughSpaceForRoomPlacement(room)) return false;

    return true;
  }

  /* checks if space for path is free
  the path should not collide with any room
  validation space is one block wider than the path size itself to avoid contact points */
  bool PathPositionIsValid(Path path, int axis)
  {
    int length = axis == 0 ? path.GetSize().x : path.GetSize().y;
    for (int i = 0; i < length; i++)
    {
      for (int j = -1; j < pathWidth + 1; j++)
      {
        Vector2Int pathTileIndex = path.position + new Vector2Int((axis ^ 1), axis) * i + new Vector2Int(axis, (axis ^ 1)) * j;
        if (pathTileIndex.x < shapeArea.x || pathTileIndex.x >= shapeArea.y || pathTileIndex.y < 0 || pathTileIndex.y >= pathMatrix.size) continue;
        if (roomMatrix.GetValue(pathTileIndex.x, pathTileIndex.y)) return false;
        if (GetDungeonShapeBlueprintPixel(pathTileIndex.x, pathTileIndex.y)) return false;

        //decide whether path crossing is allowed depending on the connection degree
        if (pathMatrix.GetValue(pathTileIndex.x, pathTileIndex.y))
        {
          if (!allowPathCrossings) return false;
          bool crossingAllowed = Random.Range(0, 1f) < (connectionDegree + connectionDegree / 4);
          if (!crossingAllowed) return false;
        }
      }
    }
    return true;
  }

  /* checks if space for the room is free
  the room should not collide with any room or path
  validation space is one block wider than the room size itself to avoid contact points */
  private bool EnoughSpaceForRoomPlacement(Room room)
  {
    for (int i = room.position.x; i < room.position.x + room.GetSize().x; i++)
    {
      for (int j = room.position.y; j < room.position.y + room.GetSize().y; j++)
      {
        if (i <= shapeArea.x || i >= shapeArea.y - 1 || j <= 0 || j >= roomMatrix.size - 1) continue;
        if (room is BluePrintRoom && !(room as BluePrintRoom).GetBlueprintPixel(i - room.position.x, j - room.position.y)) continue;
        if (GetDungeonShapeBlueprintPixel(i, j)) return false;

        if (!PositionIsFree(i, j)) return false;
        //check are arround position and check if its free
        //size of checked area depends on compressFactor
        float maxSurroundingArea = 5 >= roomDistance.y ? roomDistance.y - 1 : -4 * compressFactor + 5;
        for (int compress = 1; compress <= maxSurroundingArea; compress++)
        {
          if (i == room.position.x && !PositionIsFree(i - compress, j)) return false;
          if (i == room.GetBottomRight().x && !PositionIsFree(i + compress, j)) return false;
          if (j == room.position.y && !PositionIsFree(i, j - compress)) return false;
          if (j == room.GetBottomRight().y && !PositionIsFree(i, j + compress)) return false;
        }
      }
    }
    return true;
  }

  //checks area around position and ensures that no room clips in another
  private bool PositionIsFree(int x, int y)
  {
    if (x < 0 || x > dungeonSize - 1 || y < 0 || y > dungeonSize - 1) return true;
    if (roomMatrix.GetValue(x, y)) return false;
    if (pathMatrix.GetValue(x, y)) return false;
    return true;
  }

  private bool GetDungeonShapeBlueprintPixel(int x, int y)
  {
    if (dungeonShapeBlueprint == null) return false;
    x = Mathf.FloorToInt(x * dungeonShapeBlueprint.width / dungeonSize);
    y = Mathf.FloorToInt(y * dungeonShapeBlueprint.height / dungeonSize);
    y = dungeonShapeBlueprint.height - y - 1;
    return dungeonShapeBlueprint.GetPixel(x, y) == Color.black;
  }

  private void StartIterativeImproving()
  {
    if (roomsGraph.Count >= roomCount) return;
    for (int attempts = 0; attempts < (compressFactor + 0.005f) * 10; attempts++)
    {
      //iterate over each room and try to generate additional rooms
      for (int i = 0; i < roomsGraph.Count; i++)
      {
        Room startRoom = roomsGraph[i];
        GenerateRecursivly(startRoom);
        if (roomsGraph.Count >= roomCount) return;
      }
    }
  }

  private void GenerateAdditionalConnections()
  {
    if (connectionDegree == 0) return;
    List<Room[]> roomTuples = GraphProcessor.GenerateAdditionalConnections(roomsGraph, roomDistance.y, connectionDegree);

    foreach (var tuple in roomTuples)
    {
      TryConnectingRooms(tuple[0], tuple[1]);
    }
  }

  private void TryConnectingRooms(Room room, Room connectionRoom)
  {
    if (room.connections.Contains(connectionRoom) || connectionRoom.connections.Contains(room)) return;

    Path newPath = null;
    int direction = 0, axis = 0;
    //generate path which is oriented in y direction
    if (connectionRoom.GetBottomRight().x >= room.position.x + pathWidth && connectionRoom.position.x + pathWidth <= room.GetBottomRight().x)
    {
      axis = 1;
      int minXOffset = Mathf.Max(0, connectionRoom.position.x - room.position.x);
      int maxXOffset = Mathf.Min(room.GetSize().x - pathWidth, connectionRoom.GetBottomRight().x - room.position.x - pathWidth);
      int xOffset = UnityEngine.Random.Range(minXOffset, maxXOffset);

      direction = connectionRoom.position.y - room.position.y < 0 ? -1 : 1;
      int yOffset = direction == -1 ? -1 : room.GetSize().y;
      int distanceOffset = direction == -1 ? connectionRoom.GetSize().y : room.GetSize().y;
      int length = Mathf.Abs(room.position.y - connectionRoom.position.y) - distanceOffset;

      //if path length lies within the valid size try to create path
      if (length >= roomDistance.x && length <= roomDistance.y)
      {
        Vector2Int offset = new Vector2Int(xOffset, yOffset);
        Vector2Int pathOrigin = room.position + offset;
        newPath = CreatePath(pathOrigin, length, direction, 1);
        if (!PathPositionIsValid(newPath, 1)) newPath = null;
      }
    }
    //generate path which is oriented in x direction
    else if (connectionRoom.GetBottomRight().y >= room.position.y + pathWidth && connectionRoom.position.y + pathWidth <= room.GetBottomRight().y)
    {
      axis = 0;
      int minYOffset = Mathf.Max(0, connectionRoom.position.y - room.position.y);
      int maxYOffset = Mathf.Min(room.GetSize().y - pathWidth, connectionRoom.GetBottomRight().y - room.position.y - pathWidth);
      int yOffset = UnityEngine.Random.Range(minYOffset, maxYOffset);

      direction = connectionRoom.position.x - room.position.x < 0 ? -1 : 1;
      int xOffset = direction == -1 ? -1 : room.GetSize().x;
      int distanceOffset = direction == -1 ? connectionRoom.GetSize().x : room.GetSize().x;
      int length = Mathf.Abs(room.position.x - connectionRoom.position.x) - distanceOffset;

      //if path length lies within the valid size try to create path
      if (length >= roomDistance.x && length <= roomDistance.y)
      {
        Vector2Int offset = new Vector2Int(xOffset, yOffset);
        Vector2Int pathOrigin = room.position + offset;
        newPath = CreatePath(pathOrigin, length, direction, 0);
        if (!PathPositionIsValid(newPath, 0)) newPath = null;
      }
    }

    //if no connection is possible delete connection
    if (newPath == null)
    {
      room.connections.Remove(connectionRoom);
      connectionRoom.connections.Remove(room);
      return;
    }

    //save created path
    if (room is BluePrintRoom) FillPathInBlueprintRoom(room, newPath, axis, -direction);
    if (connectionRoom is BluePrintRoom) FillPathInBlueprintRoom(connectionRoom, newPath, axis, direction);
    newPath.AddConnections(room, connectionRoom);
    connectionRoom.AddConnection(room);
    room.AddConnection(connectionRoom);
    SavePathToBitMatrix(newPath);
    paths.Add(newPath);
  }

  Room[] outerRooms;
  List<Room> edgeRooms;
  private void GeneratePath()
  {
    outerRooms = GraphProcessor.GetEdgeRooms(roomsGraph);
    if (useFullDungeonSize)
    {
      if (Vector2Int.Distance(outerRooms[0].GetCenter(), outerRooms[2].GetCenter()) > Vector2Int.Distance(outerRooms[1].GetCenter(), outerRooms[3].GetCenter()))
      {
        startRoom = outerRooms[0];
        endRoom = outerRooms[2];
      }
      else
      {
        startRoom = outerRooms[1];
        endRoom = outerRooms[3];
      }
    }
    else
    {
      //if both rooms should be within the center, no further calculation needed
      if (startRoomPosition == RoomPosition.Center && endRoomPosition == RoomPosition.Center)
      {
        startRoom = endRoom = roomsGraph[0];
        return;
      }

      //only calculate convex hull if needed
      if (startRoomPosition == RoomPosition.Edge || endRoomPosition == RoomPosition.Edge)
        edgeRooms = GraphProcessor.CalculateConvexHull(roomsGraph, outerRooms[0]);

      //calculation is started from the most specific room to the more generic room
      //center > edge > random
      Room calculationOriginRoom = GetRoomForPathGeneration((RoomPosition)Mathf.Min((int)startRoomPosition, (int)endRoomPosition));
      bool endRoomLiesOnEdge = Mathf.Max((int)startRoomPosition, (int)endRoomPosition) == 1;
      List<Room> path = GraphProcessor.GeneratePath(roomsGraph, calculationOriginRoom, minPathLength, maxPathLength, endRoomLiesOnEdge);

      //depending on the initial room types choose start and end room from genereded path
      (startRoom, endRoom) = (int)startRoomPosition <= (int)endRoomPosition ? (path[0], path[path.Count - 1]) : (path[path.Count - 1], path[0]);

    }
  }

  private Room GetRoomForPathGeneration(RoomPosition type)
  {
    switch (type)
    {
      case RoomPosition.Edge:
        return edgeRooms[Random.Range(0, edgeRooms.Count)];
      case RoomPosition.Center:
        return roomsGraph[0];
      default:
        return roomsGraph[Random.Range(0, roomsGraph.Count)];
    }
  }

  GameObject parent;
  //iterates over filled matrix and places tiles accordingly into the world
  public void PlaceTiles()
  {
    BitMatrix combinedMatrix = roomMatrix + pathMatrix;
    parent = new GameObject("Dungeon");
    tileSetTable.Initialize();

    foreach (var room in roomsGraph.nodes)
    {
      TileSet tileSet = tileSetTable.GetRoomSet();
      for (int i = room.position.x; i < room.position.x + room.GetSize().x; i++)
      {
        for (int j = room.position.y; j < room.position.y + room.GetSize().y; j++)
        {
          if (room is BluePrintRoom && !(room as BluePrintRoom).GetBlueprintPixel(i - room.position.x, j - room.position.y)) continue;

          CheckForWallPlacement(i, j, tileSet);

          GameObject floorTile = tileSet.GetFloorTile();
          Instantiate(floorTile, new Vector3(j * tileSize, 0, i * tileSize), Quaternion.identity, parent.transform);
        }
      }
    }

    foreach (var path in paths)
    {
      TileSet tileSet = tileSetTable.GetPathSet();
      for (int i = path.position.x; i < path.position.x + path.GetSize().x; i++)
      {
        for (int j = path.position.y; j < path.position.y + path.GetSize().y; j++)
        {
          CheckForWallPlacement(i, j, tileSet);

          GameObject floorTile = tileSet.GetFloorTile();
          Instantiate(floorTile, new Vector3(j * tileSize, 0, i * tileSize), Quaternion.identity, parent.transform);
        }
      }
    }
  }

  void CheckForWallPlacement(int x, int y, TileSet tiles)
  {
    if (!roomMatrix.GetValue(x, y - 1))
    {
      PlaceWall(x * tileSize, y * tileSize - tileSize / 2f, x, y, tiles);
    }
    if (!roomMatrix.GetValue(x - 1, y))
    {
      PlaceWall(x * tileSize - tileSize / 2f, y * tileSize, x, y, tiles);
    }
    if (!roomMatrix.GetValue(x + 1, y))
    {
      PlaceWall(x * tileSize + tileSize / 2f, y * tileSize, x, y, tiles);
    }
    if (!roomMatrix.GetValue(x, y + 1))
    {
      PlaceWall(x * tileSize, y * tileSize + tileSize / 2f, x, y, tiles);
    }
  }

  void PlaceWall(float x, float y, int directionX, int directionY, TileSet tileSet)
  {
    for (int wallHeight = 0; wallHeight < dungeonHeight; wallHeight++)
    {
      GameObject wallTile = tileSet.GetWallTile();
      GameObject wall = Instantiate(wallTile, new Vector3(y, wallHeight * tileSize, x), Quaternion.identity, parent.transform);
      wall.transform.LookAt(new Vector3(directionY * tileSize, wallHeight * tileSize, directionX * tileSize));
    }
  }

  //---------------------------------------------------------------------------------------------------
  // DEBUG FUNCTIONALITY
  //---------------------------------------------------------------------------------------------------

  public void Reset()
  {
    eventRooms = new List<Room>();
    startRoom = endRoom = null;
    paths = new List<Path>();
    currentRoomCount = 1;
    iterationCount = 0;
    normalRoomProbability = 1;
    roomLookup = new List<int>();
    Destroy(parent);
  }

  List<Room> debugPath;
  public void CalculateDebugInformation()
  {
    float shapeAreaSize = Mathf.Max(dungeonSize - (shape * dungeonSize), 2 * roomDimensionX.x);
    float ratio = dungeonSize / shapeAreaSize;
    Debug.Log("Space Ratio: 1 : " + ratio);
    Debug.Log("Placed " + currentRoomCount + " rooms.");

    float connectionCount = 0;
    foreach (var room in roomsGraph.nodes)
    {
      connectionCount += room.connections.Count;
    }
    Debug.Log("Average Connection Count per room: " + connectionCount / roomsGraph.Count);

    //calculate debug path
    debugPath = GraphProcessor.GetShortestPathBetweenNodes(roomsGraph, startRoom, endRoom);

    Debug.Log("Path Length: " + (debugPath.Count - 1) + " rooms");
  }

  public int CountDeadEnds()
  {
    int count = 0;

    foreach (var node in roomsGraph.nodes)
    {
      if (node.connections.Count == 1)
      {
        count++;
        Room previousNode = node;
        Room newNode = node.connections[0];
        while (newNode.connections.Count <= 2)
        {
          count++;
          foreach (var connectingNode in newNode.connections)
          {
            if (connectingNode != previousNode)
            {
              previousNode = newNode;
              newNode = connectingNode;
              break;
            }
          }
        }
      }
    }
    return count;
  }

  public void IncreaseConnectionDegree()
  {
    connectionDegree += 0.1f;
  }

  public int GetRoomCount()
  {
    return currentRoomCount;
  }

  void SetDebugBlock(Vector2Int pos)
  {
    SetDebugBlock(pos.x, pos.y);
  }

  void SetDebugBlock(int x, int y)
  {
    Instantiate(debugCube, new Vector3(y * tileSize, 0, x * tileSize), Quaternion.identity);
  }

  public void DrawEventRooms()
  {
    Gizmos.color = Color.cyan;
    foreach (var room in eventRooms)
    {
      Gizmos.DrawSphere(room.GetCenterWorld() * tileSize, 5f);
    }
  }

  public void DrawDungeonTree()
  {
    if (roomsGraph == null) return;

    Gizmos.color = Color.blue;
    foreach (var room in roomsGraph.nodes)
    {
      Gizmos.DrawSphere(room.GetCenterWorld() * tileSize, 3f);
      foreach (var childRoom in room.connections)
      {
        Gizmos.DrawLine(room.GetCenterWorld() * tileSize, childRoom.GetCenterWorld() * tileSize);
      }
    }
  }

  public void DrawDungeonAreaOutline()
  {
    Gizmos.color = Color.red;

    float size = (dungeonSize - 1) * tileSize;
    Vector3 topRight = new Vector3(0, 0, size);
    Vector3 bottomLeft = new Vector3(size, 0, 0);
    Vector3 bottomRight = new Vector3(size, 0, size);

    Gizmos.DrawLine(Vector2.zero, topRight);
    Gizmos.DrawLine(topRight, bottomRight);
    Gizmos.DrawLine(bottomRight, bottomLeft);
    Gizmos.DrawLine(bottomLeft, Vector3.zero);
  }

  public void DrawShapeArea()
  {
    Gizmos.color = Color.green;

    float shapeAreaSize = Mathf.Max(dungeonSize - (shape * dungeonSize), 2 * roomDimensionX.x);
    float leftBorder = dungeonSize / 2 - shapeAreaSize / 2;
    float rightBorder = dungeonSize / 2 + shapeAreaSize / 2;

    Gizmos.DrawLine(new Vector3(-10, 0, tileSize * leftBorder), new Vector3(tileSize * dungeonSize + 10, 0, tileSize * leftBorder));
    Gizmos.DrawLine(new Vector3(-10, 0, tileSize * rightBorder), new Vector3(tileSize * dungeonSize + 10, 0, tileSize * rightBorder));
  }

  public void DrawPath()
  {
    if (debugPath == null) return;
    Gizmos.color = Color.red;
    for (int i = 1; i < debugPath.Count; i++)
    {
      Gizmos.DrawLine(debugPath[i - 1].GetCenterWorld() * tileSize, debugPath[i].GetCenterWorld() * tileSize);
    }

    Gizmos.color = Color.yellow;
    Gizmos.DrawSphere(startRoom.GetCenterWorld() * tileSize, 8f);
    Gizmos.color = Color.magenta;
    Gizmos.DrawSphere(endRoom.GetCenterWorld() * tileSize, 8f);
  }
}
