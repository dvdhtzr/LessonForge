import { NavLink, Outlet } from 'react-router-dom'

export default function App() {
  return (
    <div className="app">
      <header>
        <div className="topbar">
          <h1>📚 LessonForge</h1>
          <nav>
            <NavLink to="/" end className={({ isActive }) => (isActive ? 'active' : '')}>New lesson</NavLink>
            <NavLink to="/history" className={({ isActive }) => (isActive ? 'active' : '')}>My lessons</NavLink>
          </nav>
        </div>
        <p className="tagline">Standards-ready lesson plans and quizzes, researched and fact-checked for you.</p>
      </header>
      <Outlet />
    </div>
  )
}
