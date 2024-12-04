/*!
 * SvgNest
 * Licensed under the MIT license
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Xml;

public class SvgNest
{
    private XmlDocument svg;
    public XmlNode Style { get; private set; }
    private List<XmlNode> parts;
    private List<Polygon> tree;
    private Polygon binPolygon;
    private Rectangle binBounds;
    private Dictionary<string, List<Polygon>> nfpCache;
    private Config config;
    public bool Working { get; private set; }
    private GeneticAlgorithm GA;
    private Placement best;
    private System.Timers.Timer workerTimer;
    private double progress;

    public SvgNest()
    {
        svg = null;
        Style = null;
        parts = null;
        tree = null;
        binPolygon = null;
        binBounds = null;
        nfpCache = new Dictionary<string, List<Polygon>>();
        config = new Config
        {
            ClipperScale = 10000000,
            CurveTolerance = 0.3,
            Spacing = 0,
            Rotations = 4,
            PopulationSize = 10,
            MutationRate = 10,
            UseHoles = false,
            ExploreConcave = false
        };
        Working = false;
        GA = null;
        best = null;
        workerTimer = null;
        progress = 0;
    }

    public XmlDocument ParseSvg(string svgString)
    {
        Stop();

        binPolygon = null;
        tree = null;

        svg = SvgParser.Load(svgString);
        Style = SvgParser.GetStyle();
        svg = SvgParser.Clean();
        tree = GetParts(svg.DocumentElement.ChildNodes);

        return svg;
    }

    public void SetBin(XmlNode element)
    {
        if (svg == null)
        {
            return;
        }
        binPolygon = SvgParser.Polygonify(element);
    }

    public Config Config(Config c)
    {
        if (c == null)
        {
            return config;
        }

        if (c.CurveTolerance != 0)
        {
            config.CurveTolerance = c.CurveTolerance;
        }

        if (c.Spacing != 0)
        {
            config.Spacing = c.Spacing;
        }

        if (c.Rotations > 0)
        {
            config.Rotations = c.Rotations;
        }

        if (c.PopulationSize > 2)
        {
            config.PopulationSize = c.PopulationSize;
        }

        if (c.MutationRate > 0)
        {
            config.MutationRate = c.MutationRate;
        }

        config.UseHoles = c.UseHoles;
        config.ExploreConcave = c.ExploreConcave;

        SvgParser.Config(new SvgParserConfig { Tolerance = config.CurveTolerance });

        best = null;
        nfpCache = new Dictionary<string, List<Polygon>>();
        binPolygon = null;
        GA = null;

        return config;
    }

    public bool Start(Action<double> progressCallback, Action<List<XmlDocument>, double, int, int> displayCallback)
    {
        if (svg == null || binPolygon == null)
        {
            return false;
        }

        parts = svg.DocumentElement.ChildNodes.Cast<XmlNode>().ToList();
        int binIndex = parts.IndexOf(binPolygon);

        if (binIndex >= 0)
        {
            parts.RemoveAt(binIndex);
        }

        tree = GetParts(parts);

        OffsetTree(tree, 0.5 * config.Spacing, PolygonOffset);

        binPolygon = CleanPolygon(binPolygon);

        if (binPolygon == null || binPolygon.Count < 3)
        {
            return false;
        }

        binBounds = GeometryUtil.GetPolygonBounds(binPolygon);

        if (config.Spacing > 0)
        {
            var offsetBin = PolygonOffset(binPolygon, -0.5 * config.Spacing);
            if (offsetBin.Count == 1)
            {
                binPolygon = offsetBin[0];
            }
        }

        binPolygon.Id = -1;

        AlignBinPolygon(binPolygon);

        tree.ForEach(p =>
        {
            if (GeometryUtil.PolygonArea(p) > 0)
            {
                p.Reverse();
            }
        });

        Working = false;

        workerTimer = new System.Timers.Timer(100);
        workerTimer.Elapsed += (sender, e) =>
        {
            if (!Working)
            {
                LaunchWorkers(tree, binPolygon, config, progressCallback, displayCallback);
                Working = true;
            }

            progressCallback(progress);
        };
        workerTimer.Start();

        return true;
    }

    private void LaunchWorkers(List<Polygon> tree, Polygon binPolygon, Config config, Action<double> progressCallback, Action<List<XmlDocument>, double, int, int> displayCallback)
    {
        if (GA == null)
        {
            var adam = tree.OrderByDescending(p => Math.Abs(GeometryUtil.PolygonArea(p))).ToList();
            GA = new GeneticAlgorithm(adam, binPolygon, config);
        }

        var individual = GA.Population.FirstOrDefault(i => i.Fitness == null);

        if (individual == null)
        {
            GA.Generation();
            individual = GA.Population[1];
        }

        var placelist = individual.Placement;
        var rotations = individual.Rotation;

        var ids = placelist.Select(p => p.Id).ToList();
        placelist.ForEach(p => p.Rotation = rotations[placelist.IndexOf(p)]);

        var nfpPairs = new List<NfpPair>();
        var newCache = new Dictionary<string, List<Polygon>>();

        foreach (var part in placelist)
        {
            var key = new NfpKey { A = binPolygon.Id, B = part.Id, Inside = true, ARotation = 0, BRotation = rotations[placelist.IndexOf(part)] };
            if (!nfpCache.ContainsKey(key.ToString()))
            {
                nfpPairs.Add(new NfpPair { A = binPolygon, B = part, Key = key });
            }
            else
            {
                newCache[key.ToString()] = nfpCache[key.ToString()];
            }

            foreach (var placed in placelist.Take(placelist.IndexOf(part)))
            {
                key = new NfpKey { A = placed.Id, B = part.Id, Inside = false, ARotation = rotations[placelist.IndexOf(placed)], BRotation = rotations[placelist.IndexOf(part)] };
                if (!nfpCache.ContainsKey(key.ToString()))
                {
                    nfpPairs.Add(new NfpPair { A = placed, B = part, Key = key });
                }
                else
                {
                    newCache[key.ToString()] = nfpCache[key.ToString()];
                }
            }
        }

        nfpCache = newCache;

        var worker = new PlacementWorker(binPolygon, placelist, ids, rotations, config, nfpCache);

        var p = new Parallel<NfpPair>(nfpPairs, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        });

        p.ForAll(pair =>
        {
            if (pair == null)
            {
                return;
            }

            var A = GeometryUtil.RotatePolygon(pair.A, pair.Key.ARotation);
            var B = GeometryUtil.RotatePolygon(pair.B, pair.Key.BRotation);

            List<Polygon> nfp = null;

            if (pair.Key.Inside)
            {
                nfp = GeometryUtil.NoFitPolygon(A, B, true, config.ExploreConcave);
                if (nfp != null && nfp.Count > 0)
                {
                    nfp.ForEach(polygon =>
                    {
                        if (GeometryUtil.PolygonArea(polygon) > 0)
                        {
                            polygon.Reverse();
                        }
                    });
                }
            }
            else
            {
                nfp = GeometryUtil.NoFitPolygon(A, B, false, config.ExploreConcave);
                if (nfp != null && nfp.Count > 0)
                {
                    nfp.ForEach(polygon =>
                    {
                        if (GeometryUtil.PolygonArea(polygon) > 0)
                        {
                            polygon.Reverse();
                        }
                    });
                }
            }

            if (nfp != null)
            {
                nfpCache[pair.Key.ToString()] = nfp;
            }
        });

        worker.NfpCache = nfpCache;

        var placements = worker.PlacePaths(placelist);

        if (placements != null && placements.Count > 0)
        {
            individual.Fitness = placements[0].Fitness;
            var bestResult = placements.OrderBy(p => p.Fitness).First();

            if (best == null || bestResult.Fitness < best.Fitness)
            {
                best = bestResult;

                var placedArea = 0.0;
                var totalArea = 0.0;
                var numParts = placelist.Count;
                var numPlacedParts = 0;

                foreach (var placement in best.Placements)
                {
                    totalArea += Math.Abs(GeometryUtil.PolygonArea(binPolygon));
                    foreach (var p in placement)
                    {
                        placedArea += Math.Abs(GeometryUtil.PolygonArea(tree[p.Id]));
                        numPlacedParts++;
                    }
                }

                displayCallback(ApplyPlacement(best.Placements), placedArea / totalArea, numPlacedParts, numParts);
            }
            else
            {
                displayCallback(null, 0, 0, 0);
            }
        }

        Working = false;
    }

    private List<Polygon> GetParts(XmlNodeList paths)
    {
        var polygons = new List<Polygon>();

        foreach (XmlNode path in paths)
        {
            var poly = SvgParser.Polygonify(path);
            poly = CleanPolygon(poly);

            if (poly != null && poly.Count > 2 && Math.Abs(GeometryUtil.PolygonArea(poly)) > config.CurveTolerance * config.CurveTolerance)
            {
                poly.Source = path;
                polygons.Add(poly);
            }
        }

        ToTree(polygons);

        return polygons;
    }

    private void ToTree(List<Polygon> list, int idStart = 0)
    {
        var parents = new List<Polygon>();
        var id = idStart;

        foreach (var p in list)
        {
            var isChild = false;
            foreach (var parent in list)
            {
                if (parent == p)
                {
                    continue;
                }
                if (GeometryUtil.PointInPolygon(p[0], parent))
                {
                    if (parent.Children == null)
                    {
                        parent.Children = new List<Polygon>();
                    }
                    parent.Children.Add(p);
                    p.Parent = parent;
                    isChild = true;
                    break;
                }
            }

            if (!isChild)
            {
                parents.Add(p);
            }
        }

        list.RemoveAll(p => !parents.Contains(p));

        foreach (var parent in parents)
        {
            parent.Id = id++;
        }

        foreach (var parent in parents)
        {
            if (parent.Children != null)
            {
                ToTree(parent.Children, id);
            }
        }
    }

    private List<Polygon> PolygonOffset(Polygon polygon, double offset)
    {
        if (offset == 0 || GeometryUtil.AlmostEqual(offset, 0))
        {
            return new List<Polygon> { polygon };
        }

        var p = SvgToClipper(polygon);

        var co = new ClipperLib.ClipperOffset(2, config.CurveTolerance * config.ClipperScale);
        co.AddPath(p, ClipperLib.JoinType.jtRound, ClipperLib.EndType.etClosedPolygon);

        var newPaths = new List<List<ClipperLib.IntPoint>>();
        co.Execute(ref newPaths, offset * config.ClipperScale);

        return newPaths.Select(ClipperToSvg).ToList();
    }

    private Polygon CleanPolygon(Polygon polygon)
    {
        var p = SvgToClipper(polygon);
        var simple = ClipperLib.Clipper.SimplifyPolygon(p, ClipperLib.PolyFillType.pftNonZero);

        if (simple == null || simple.Count == 0)
        {
            return null;
        }

        var biggest = simple.OrderByDescending(ClipperLib.Clipper.Area).First();
        var clean = ClipperLib.Clipper.CleanPolygon(biggest, config.CurveTolerance * config.ClipperScale);

        if (clean == null || clean.Count == 0)
        {
            return null;
        }

        return ClipperToSvg(clean);
    }

    private List<ClipperLib.IntPoint> SvgToClipper(Polygon polygon)
    {
        var clip = polygon.Select(p => new ClipperLib.IntPoint(p.X, p.Y)).ToList();
        ClipperLib.Clipper.ScaleUpPath(clip, config.ClipperScale);
        return clip;
    }

    private Polygon ClipperToSvg(List<ClipperLib.IntPoint> polygon)
    {
        return polygon.Select(p => new Point(p.X / config.ClipperScale, p.Y / config.ClipperScale)).ToList();
    }

    private List<XmlDocument> ApplyPlacement(List<List<Placement>> placement)
    {
        var clone = parts.Select(p => p.CloneNode(false)).ToList();
        var svgList = new List<XmlDocument>();

        foreach (var place in placement)
        {
            var newSvg = (XmlDocument)svg.CloneNode(false);
            newSvg.DocumentElement.SetAttribute("viewBox", $"0 0 {binBounds.Width} {binBounds.Height}");
            newSvg.DocumentElement.SetAttribute("width", $"{binBounds.Width}px");
            newSvg.DocumentElement.SetAttribute("height", $"{binBounds.Height}px");

            var binClone = binPolygon.Source.CloneNode(false);
            binClone.Attributes.Append(newSvg.CreateAttribute("class")).Value = "bin";
            binClone.Attributes.Append(newSvg.CreateAttribute("transform")).Value = $"translate({-binBounds.X} {-binBounds.Y})";
            newSvg.DocumentElement.AppendChild(binClone);

            foreach (var p in place)
            {
                var part = tree[p.Id];
                var partGroup = newSvg.CreateElement("g");
                partGroup.SetAttribute("transform", $"translate({p.X} {p.Y}) rotate({p.Rotation})");
                partGroup.AppendChild(clone[part.Source]);

                if (part.Children != null && part.Children.Count > 0)
                {
                    var flattened = FlattenTree(part.Children, true);
                    foreach (var child in flattened)
                    {
                        var c = clone[child.Source];
                        if (child.Hole && (c.Attributes["class"] == null || !c.Attributes["class"].Value.Contains("hole")))
                        {
                            c.Attributes.Append(newSvg.CreateAttribute("class")).Value += " hole";
                        }
                        partGroup.AppendChild(c);
                    }
                }

                newSvg.DocumentElement.AppendChild(partGroup);
            }

            svgList.Add(newSvg);
        }

        return svgList;
    }

    private List<Polygon> FlattenTree(List<Polygon> tree, bool hole)
    {
        var flat = new List<Polygon>();
        foreach (var t in tree)
        {
            flat.Add(t);
            t.Hole = hole;
            if (t.Children != null && t.Children.Count > 0)
            {
                flat.AddRange(FlattenTree(t.Children, !hole));
            }
        }
        return flat;
    }

    public void Stop()
    {
        Working = false;
        workerTimer?.Stop();
    }

    private void OffsetTree(List<Polygon> tree, double offset, Func<Polygon, double, List<Polygon>> offsetFunction)
    {
        foreach (var t in tree)
        {
            var offsetPaths = offsetFunction(t, offset);
            if (offsetPaths.Count == 1)
            {
                t.Clear();
                t.AddRange(offsetPaths[0]);
            }

            if (t.Children != null && t.Children.Count > 0)
            {
                OffsetTree(t.Children, -offset, offsetFunction);
            }
        }
    }

    private void AlignBinPolygon(Polygon binPolygon)
    {
        var xBinMax = binPolygon.Max(p => p.X);
        var xBinMin = binPolygon.Min(p => p.X);
        var yBinMax = binPolygon.Max(p => p.Y);
        var yBinMin = binPolygon.Min(p => p.Y);

        foreach (var point in binPolygon)
        {
            point.X -= xBinMin;
            point.Y -= yBinMin;
        }

        binPolygon.Width = xBinMax - xBinMin;
        binPolygon.Height = yBinMax - yBinMin;

        if (GeometryUtil.PolygonArea(binPolygon) > 0)
        {
            binPolygon.Reverse();
        }
    }
}

public class Config
{
    public double ClipperScale { get; set; }
    public double CurveTolerance { get; set; }
    public double Spacing { get; set; }
    public int Rotations { get; set; }
    public int PopulationSize { get; set; }
    public int MutationRate { get; set; }
    public bool UseHoles { get; set; }
    public bool ExploreConcave { get; set; }
}

public class Polygon : List<Point>
{
    public int Id { get; set; }
    public XmlNode Source { get; set; }
    public List<Polygon> Children { get; set; }
    public Polygon Parent { get; set; }
    public bool Hole { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class Point
{
    public double X { get; set; }
    public double Y { get; set; }

    public Point(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public class Placement
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public double Fitness { get; set; }
    public List<List<Placement>> Placements { get; set; }
}

public class NfpPair
{
    public Polygon A { get; set; }
    public Polygon B { get; set; }
    public NfpKey Key { get; set; }
}

public class NfpKey
{
    public int A { get; set; }
    public int B { get; set; }
    public bool Inside { get; set; }
    public double ARotation { get; set; }
    public double BRotation { get; set; }

    public override string ToString()
    {
        return $"{A}-{B}-{Inside}-{ARotation}-{BRotation}";
    }
}

public class GeneticAlgorithm
{
    public List<Individual> Population { get; private set; }
    private Config config;
    private Rectangle binBounds;

    public GeneticAlgorithm(List<Polygon> adam, Polygon bin, Config config)
    {
        this.config = config;
        binBounds = GeometryUtil.GetPolygonBounds(bin);

        var angles = adam.Select(p => RandomAngle(p)).ToList();
        Population = new List<Individual> { new Individual { Placement = adam, Rotation = angles } };

        while (Population.Count < config.PopulationSize)
        {
            var mutant = Mutate(Population[0]);
            Population.Add(mutant);
        }
    }

    private double RandomAngle;
        }