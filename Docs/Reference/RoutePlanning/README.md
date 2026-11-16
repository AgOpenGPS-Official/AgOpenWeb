# Route planning: reference papers

The papers behind the pre-computed route work (#19), kept here so the project has them.
What each one gives us is summarised in #19.

The first six are open access under CC BY. The last three are not under an open licence
(an author's pre-print, a conference paper that is free to read, an MSc thesis): they are
kept for reference, each with the link to where it is published.

## In this folder (CC BY)

| File | Paper | Used for |
|---|---|---|
| `Zhou-2020-metric-map-generation.pdf` | Zhou, Jensen, Bochtis, Nørremark, Kateris, Sørensen (2020). *Metric Map Generation for Autonomous Field Operations.* Agronomy 10, 83. [doi:10.3390/agronomy10010083](https://doi.org/10.3390/agronomy10010083) | The field representation: headland rings round the field and each obstacle, tracks clipped into segments |
| `Nilsson-Zhou-2020-decision-support-tool.pdf` | Nilsson, Zhou (2020). *Decision Support Tool for Operational Planning of Field Operations.* Agronomy 10, 229. [doi:10.3390/agronomy10020229](https://doi.org/10.3390/agronomy10020229) | The pipeline as a product; the figures a plan is judged by |
| `Vahdanjoo-2020-multiple-depots.pdf` | Vahdanjoo, Zhou, Sørensen (2020). *Route Planning for Agricultural Machines with Multiple Depots: Manure Application Case Study.* Agronomy 10, 1608. [doi:10.3390/agronomy10101608](https://doi.org/10.3390/agronomy10101608) | Track order as an optimisation problem (simulated annealing against B-patterns); refills |
| `Khosravani-2020-benchmark-field.pdf` | Khosravani Moghadam, Vahdanjoo, Jensen, Sharifi, Sørensen (2020). *An Arable Field for Benchmarking of Metaheuristic Algorithms for Capacitated Coverage Path Planning Problems.* Agronomy 10, 1454. [doi:10.3390/agronomy10101454](https://doi.org/10.3390/agronomy10101454) | An 8-track field with its coordinates and the true optimum: a test oracle for track ordering |
| `Hoffmann-2023-optimal-coverage-path-planning.pdf` | Höffmann, Patel, Büskens (2023). *Optimal Coverage Path Planning for Agricultural Vehicles with Curvature Constraints.* Agriculture 13, 2112. [doi:10.3390/agriculture13112112](https://doi.org/10.3390/agriculture13112112) | The whole pipeline: cells, cell order with entry corners, links along the headland tracks |
| `Hameed-2013-fields-with-obstacle-areas.pdf` | Hameed, Bochtis, Sørensen (2013). *An Optimized Field Coverage Planning Approach for Navigation of Agricultural Robots in Fields Involving Obstacle Areas.* Int. J. Advanced Robotic Systems 10, 231. [doi:10.5772/56248](https://doi.org/10.5772/56248) | Blocks from track segments (no sweep line); headland passes first or last by operation |

## In this folder (not CC BY)

| File | Paper | Used for |
|---|---|---|
| `Zhou-2014-multiple-obstacle-areas-preprint.pdf` | Zhou, Jensen, Sørensen, Busato, Bochtis (2014). *Agricultural operations planning in fields with multiple obstacle areas.* Computers and Electronics in Agriculture 109. [doi:10.1016/j.compag.2014.08.013](https://doi.org/10.1016/j.compag.2014.08.013). Author's pre-print from the University of Turin's repository, [hdl.handle.net/2318/1520371](http://hdl.handle.net/2318/1520371) | The four obstacle types (A to D), blocks cut only at large obstacles, links between blocks along a shared headland; checked against two farmers' logged jobs |
| `Liu-2018-small-obstacles.pdf` | Liu, Zhao, Du, Cao, Zhu, Mao (2018). *Research on static path planning method of small obstacles for automatic navigation of agricultural machinery.* IFAC-PapersOnLine 51(17), 673–677. [doi:10.1016/j.ifacol.2018.08.119](https://doi.org/10.1016/j.ifacol.2018.08.119). © IFAC | The three-arc path round a small obstacle (simulation only) |
| `Versleijen-2019-fields-with-obstacles.pdf` | Versleijen (2019). *Path planning on agricultural fields with obstacles.* MSc thesis GIRS-2019-21, Wageningen University (Wageningen's repository, edepot 504253) | Obstacle categories with a go-round for small ones; thresholds against slivers when a field is cut into cells |

## Not read yet

Named in #19 or cited by the papers above, and worth reading before the parts they cover are
designed: Oksanen (2007, trapezoid splitting, tractor-trailer headland turns), Bochtis and
Vougioukas (2008, B-patterns), Liu and Palmer (the "four implement widths" rule), Bakhtiari
et al. (2011, B-patterns by ant colony), Driscoll (2011), Höffmann et al. (2024, review of
guidance track generation).
