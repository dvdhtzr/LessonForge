import { NavLink, Outlet } from 'react-router-dom'

export default function App() {
  return (
    <div className="app">
      <header className="topbar">
        <h1>📚 LessonForge</h1>
        <nav>
          <NavLink to="/" end className={({ isActive }) => (isActive ? 'active' : '')}>New lesson</NavLink>
          <NavLink to="/history" className={({ isActive }) => (isActive ? 'active' : '')}>History</NavLink>
        </nav>
      </header>
      <Outlet />
    </div>
  )
}
