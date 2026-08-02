CustomSkin 人工绘制模板

1. 正式皮肤只编辑 icon.png、textures/head.png、textures/body.png、textures/legs.png 和 manifest.json；references/reference.png 只是画板参考图，不进入分享包。
2. PNG 必须保持原尺寸、8 位 RGBA 和透明背景，不要缩放画布或增加第二套朝向。
3. 人物统一朝屏幕右侧；Terraria 会在朝左时自动镜像。
4. Head 和 Legs 各为 20 个 40x56 格纵向排列；Body 为 9x4、共 36 个 40x56 复合部件格。
5. Head、躯干、腿、肩和手臂在同一个 40x56 坐标系中重叠，不要把完整人物按上中下切开。
6. guides 下的 *-guide.png 是可叠在原图上的 1×参考边界；*-state-map.png 是放大后的格子状态索引图。animation-map.json 记录 Idle、动作、跳跃、移动与武器手臂的精确关系，frame-map.json 记录每格的底层部件语义。
7. guides 不限制绘制范围，也不会进入分享包。长发、披风、角和其他扩展轮廓可以超出 guide，自由发挥后请在游戏中检查动作和武器遮挡。
8. 在游戏内画板点击“导入参考图”可选择一张静态 PNG。参考图支持透明预览、逐动作帧定位、镜像、描图以及写入当前选中部位；AI 和 PerfectPixel 不是 Mod 依赖。
9. 动画播放时禁止把参考图叠加或替换到皮肤。请先暂停并确认当前帧、部位、朝向和躯干类型；每次写入都可以整步撤销。
10. 保存图片后回到游戏，点击“刷新并使用”。需要分享时点击“导出分享包”。

许可证不是必填项。需要时可自行添加 LICENSE.txt，并在 manifest.json 中添加 license 字段。
